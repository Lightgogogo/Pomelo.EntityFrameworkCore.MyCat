﻿// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Query.ExpressionTranslators.Internal
{
    public class MyCatStringToUpperTranslator : ParameterlessInstanceMethodCallTranslator
    {
        public MyCatStringToUpperTranslator()
            : base(declaringType: typeof(string), clrMethodName: "ToUpper", sqlFunctionName: "UPPER")
        {
        }
    }
}
