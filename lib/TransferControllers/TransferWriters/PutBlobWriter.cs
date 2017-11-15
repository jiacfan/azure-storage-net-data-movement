﻿//------------------------------------------------------------------------------
// <copyright file="PutBlobWriter.cs" company="Microsoft">
//    Copyright (c) Microsoft Corporation
// </copyright>
//------------------------------------------------------------------------------

namespace Microsoft.WindowsAzure.Storage.DataMovement.TransferControllers
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.WindowsAzure.Storage.Blob;

    internal sealed class PutBlobWriter : TransferReaderWriterBase
    {
        private volatile bool hasWork;
        private volatile State state;
        private readonly AzureBlobLocation destLocation;
        private readonly CloudBlockBlob blockBlob;

        public PutBlobWriter(
            TransferScheduler scheduler,
            SyncTransferController controller,
            CancellationToken cancellationToken)
            : base(scheduler, controller, cancellationToken)
        {
            this.destLocation = this.SharedTransferData.TransferJob.Destination as AzureBlobLocation;
            this.blockBlob = this.destLocation?.Blob as CloudBlockBlob;

            Debug.Assert(null != this.blockBlob, "The destination is not a block blob while initializing a PutBlobWriter instance.");

            // Note: File size equal to 0 is a special case, which should be handled by BlockBlobWriter. 
            Debug.Assert(
                this.SharedTransferData.TotalLength > 0,
                "PutBlobWriter should be initialized after this.SharedTransferData.TotalLength is initialized.");

            Debug.Assert(
                this.SharedTransferData.TotalLength <= Constants.SingleRequestBlobSizeThreshold, 
                $"The data to transfer is larger than {Constants.SingleRequestBlobSizeThreshold} (KB) while initializing a PutBlobWriter instance.");

            this.state = State.FetchAttributes;
            this.hasWork = true;
        }

        private enum State
        {
            FetchAttributes,
            UploadBlob,
            Error,
            Finished
        };

        public override bool PreProcessed
        {
            get;
            protected set;
        }

        public override bool HasWork => this.hasWork &&
                                        (!this.PreProcessed 
                                         || (this.state == State.UploadBlob && this.SharedTransferData.AvailableData.Any() && null != this.SharedTransferData.Attributes));

        public override bool IsFinished => State.Error == this.state || State.Finished == this.state;

        public override async Task DoWorkInternalAsync()
        {
            switch (this.state)
            {
                case State.FetchAttributes:
                    await this.FetchAttributesAsync();
                    break;
                case State.UploadBlob:
                    await this.UploadBlobAsync();
                    break;
                case State.Error:
                case State.Finished:
                default:
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private async Task FetchAttributesAsync()
        {
            Debug.Assert(
                this.state == State.FetchAttributes,
                "FetchAttributesAsync called, but state isn't FetchAttributes",
                "Current state is {0}",
                this.state);

            this.hasWork = false;

            if (this.SharedTransferData.TotalLength > Constants.MaxBlockBlobFileSize)
            {
                string exceptionMessage = string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.BlobFileSizeTooLargeException,
                            Utils.BytesToHumanReadableSize(this.SharedTransferData.TotalLength),
                            Resources.BlockBlob,
                            Utils.BytesToHumanReadableSize(Constants.MaxBlockBlobFileSize));

                throw new TransferException(
                        TransferErrorCode.UploadSourceFileSizeTooLarge,
                        exceptionMessage);
            }

            if (!this.Controller.IsForceOverwrite)
            {
                AccessCondition accessCondition = Utils.GenerateConditionWithCustomerCondition(
                    this.destLocation.AccessCondition,
                    this.destLocation.CheckedAccessCondition);

                try
                {
                    await this.destLocation.Blob.FetchAttributesAsync(
                        accessCondition,
                        Utils.GenerateBlobRequestOptions(this.destLocation.BlobRequestOptions),
                        Utils.GenerateOperationContext(this.Controller.TransferContext),
                        this.CancellationToken);
                }
#if EXPECT_INTERNAL_WRAPPEDSTORAGEEXCEPTION
                catch (Exception e) when (e is StorageException || (e is AggregateException && e.InnerException is StorageException))
                {
                    var se = e as StorageException ?? e.InnerException as StorageException;
#else
                catch (StorageException se)
                {
#endif
                    this.HandleFetchAttributesResult(se);
                    return;
                }
            }

            this.HandleFetchAttributesResult(null);
        }

        private void HandleFetchAttributesResult(Exception e)
        {
            bool existingBlob = !this.Controller.IsForceOverwrite;

            if (null != e)
            {
                StorageException se = e as StorageException;

                if (null != se)
                {
                    // Getting a storage exception is expected if the blob doesn't
                    // exist. In this case we won't error out, but set the 
                    // existingBlob flag to false to indicate we're uploading
                    // a new blob instead of overwriting an existing blob.
                    if (null != se.RequestInformation &&
                        se.RequestInformation.HttpStatusCode == (int)HttpStatusCode.NotFound)
                    {
                        existingBlob = false;
                    }
                    else if ((0 == string.Compare(se.Message, Constants.BlobTypeMismatch, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(Resources.DestinationBlobTypeNotMatch, se);
                    }
                    else
                    {
                        throw se;
                    }
                }
                else
                {
                    throw e;
                }
            }

            this.destLocation.CheckedAccessCondition = true;

            if (!this.Controller.IsForceOverwrite)
            {
                // If destination file exists, query user whether to overwrite it.
                this.Controller.CheckOverwrite(
                    existingBlob,
                    this.SharedTransferData.TransferJob.Source.Instance,
                    this.destLocation.Blob);
            }

            this.Controller.UpdateProgressAddBytesTransferred(0);

            if (existingBlob)
            {
                if (this.destLocation.Blob.Properties.BlobType == BlobType.Unspecified)
                {
                    throw new InvalidOperationException(Resources.FailedToGetBlobTypeException);
                }
                if (this.destLocation.Blob.Properties.BlobType != BlobType.BlockBlob)
                {
                    throw new InvalidOperationException(Resources.DestinationBlobTypeNotMatch);
                }

                Debug.Assert(
                    this.destLocation.Blob.Properties.BlobType == BlobType.BlockBlob,
                    "BlobType should be BlockBlob if we reach here.");
            }
            
            this.state = State.UploadBlob;
            this.PreProcessed = true;
            this.hasWork = true;
        }
        
        private async Task UploadBlobAsync()
        {
            Debug.Assert(
                State.UploadBlob == this.state || State.Error == this.state,
                "UploadBlobAsync called but state is not UploadBlob nor Error.",
                "Current state is {0}",
                this.state);

            TransferData transferData = this.GetFirstAvailable();

            if (null != transferData)
            {
                using (transferData)
                {
                    if (transferData.MemoryBuffer.Length == 1)
                    {
                        transferData.Stream = new MemoryStream(transferData.MemoryBuffer[0], 0, transferData.Length);
                    }
                    else
                    {
                        transferData.Stream = new ChunkedMemoryStream(transferData.MemoryBuffer, 0, transferData.Length);
                    }
                    
                    Utils.SetAttributes(this.blockBlob, this.SharedTransferData.Attributes);
                    await this.Controller.SetCustomAttributesAsync(this.blockBlob);

                    await this.blockBlob.UploadFromStreamAsync(
                        transferData.Stream,
                        Utils.GenerateConditionWithCustomerCondition(this.destLocation.AccessCondition, true),
                        Utils.GenerateBlobRequestOptions(this.destLocation.BlobRequestOptions),
                        Utils.GenerateOperationContext(this.Controller.TransferContext),
                        this.CancellationToken);
                }

                this.Controller.UpdateProgress(() =>
                {
                    lock (this.SharedTransferData.TransferJob.CheckPoint.TransferWindowLock)
                    {
                        this.SharedTransferData.TransferJob.CheckPoint.TransferWindow.Remove(transferData.StartOffset);
                    }
                    this.SharedTransferData.TransferJob.Transfer.UpdateJournal();

                    // update progress
                    this.Controller.UpdateProgressAddBytesTransferred(transferData.Length);
                });

                this.SetFinish();
            }
        }

        private void SetFinish()
        {
            this.state = State.Finished;
            this.NotifyFinished(null);
            this.hasWork = false;
        }
    }
}