﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using BuildXL.Cache.ContentStore.Hashing;

namespace BuildXL.Cache.ContentStore.Stores
{
    /// <summary>
    /// This class is specifically for usage within <see cref="ContentDirectorySnapshot{T}"/>. It is a reference type on purpose, in order to avoid issues
    /// with the maximum object size limit.
    /// </summary>
    /// <typeparam name="T">Type tagged with a hash</typeparam>
    public class PayloadFromDisk<T>
    {
        /// <summary>
        /// Hash for the <see cref="Payload"/>
        /// </summary>
        public ContentHash Hash { get; }

        /// <summary>
        /// Information for which <see cref="Hash"/> applies
        /// </summary>
        public T Payload { get; }

        /// <nodoc />
        public PayloadFromDisk(ContentHash hash, T payload)
        {
            Hash = hash;
            Payload = payload;
        }
    }

    /// <summary>
    /// Compares two PayloadFromDisk instances by checking at their hashes
    /// </summary>
    public class ByHashPayloadFromDiskComparer<T> : IComparer<PayloadFromDisk<T>>
    {
        /// <inheritdoc />
        public int Compare([AllowNull]PayloadFromDisk<T> left, [AllowNull]PayloadFromDisk<T> right) => left!.Hash.CompareTo(right!.Hash);
    }
}
