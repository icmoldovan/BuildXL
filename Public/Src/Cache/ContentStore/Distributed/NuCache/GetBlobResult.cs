﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <nodoc />
    public class GetBlobResult : BoolResult
    {
        /// <nodoc />
        public ContentHash Hash { get; }

        /// <nodoc />
        public byte[]? Blob { get; }

        /// <nodoc />
        public GetBlobResult(ContentHash hash, byte[]? blob)
            : base(succeeded: true)
        {
            Hash = hash;
            Blob = blob;
        }

        /// <nodoc />
        public GetBlobResult(string errorMessage, string? diagnostics = null)
            : base(errorMessage, diagnostics)
        {

        }

        /// <nodoc />
        public GetBlobResult(ResultBase other, string message)
            : base(other, message)
        {
        }

        /// <nodoc />
        public GetBlobResult(ResultBase other, string message, ContentHash hash)
            : base(other, message)
        {
            Hash = hash;
        }

        /// <nodoc />
        public GetBlobResult(ResultBase other, ContentHash hash)
            : base(other)
        {
            Hash = hash;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (Succeeded)
            {
                return $"Hash=[{Hash.ToShortString()}] Size=[{Blob?.Length ?? -1}]";
            }

            return $"Hash=[{Hash.ToShortString()}]. Error=[{ErrorMessage}]";
        }
    }
}
