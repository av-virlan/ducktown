﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.Serialization;
using System.Threading;

namespace Microsoft.CodeAnalysis.EditAndContinue;

[DataContract]
internal readonly record struct DebuggingSessionId([property: DataMember] int Ordinal)
{
    public override string ToString()
        => Ordinal.ToString();
}
