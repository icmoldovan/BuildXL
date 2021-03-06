// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using BuildXL.Utilities.Configuration;
using Xunit;

namespace Test.BuildXL
{
    public class ExitKindToExitCodeTests
    {
        [Fact]
        public void AllExitKindsAccountedFor()
        {
            foreach (ExitKind exitKind in Enum.GetValues(typeof(ExitKind)))
            {
                // No crash = happy test
                ExitCode.FromExitKind(exitKind);
            }
        }
    }
}
