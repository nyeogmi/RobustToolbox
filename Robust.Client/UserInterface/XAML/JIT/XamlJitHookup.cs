using System;
using System.Collections.Generic;
using System.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Reflection;
using XamlX.TypeSystem;

namespace Robust.Client.UserInterface.XAML.JIT
{
    public sealed class XamlJitHookup
    {
        [Dependency] IXamlJitManager _xamlJitManager = default!;


        public void PopulateJit(Type t, object o)
        {
            _xamlJitManager.PopulateJit(t, o);
        }
    }
}
