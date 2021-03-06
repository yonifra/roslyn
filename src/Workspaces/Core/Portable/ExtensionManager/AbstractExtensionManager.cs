﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Extensions
{
    internal abstract class AbstractExtensionManager : IExtensionManager
    {
        private readonly ConcurrentSet<object> disabledProviders = new ConcurrentSet<object>(ReferenceEqualityComparer.Instance);

        protected AbstractExtensionManager()
        {
        }

        private void DisableProvider(object provider)
        {
            disabledProviders.Add(provider);
        }

        public bool IsDisabled(object provider)
        {
            return disabledProviders.Contains(provider);
        }

        public virtual bool CanHandleException(object provider, Exception exception)
        {
            return true;
        }

        public virtual void HandleException(object provider, Exception exception)
        {
            DisableProvider(provider);
        }
    }
}
