﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.SandboxedProcessExecutor.Tracing
{
    // disable warning regarding 'missing XML comments on public API'. We don't need docs for these values
#pragma warning disable 1591

    /// <summary>
    /// Defines event IDs corresponding to events in <see cref="Logger" />.
    /// </summary>
    public enum LogEventId : ushort
    {
        // RESERVED TO [8700, 8799] (BuildXL.SandboxedProcessExecutor)
        SandboxedProcessExecutorInvoked = 8700,
        SandboxedProcessExecutorCatastrophicFailure = 8701,
    }
}
