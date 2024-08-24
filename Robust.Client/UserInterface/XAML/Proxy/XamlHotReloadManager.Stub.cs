﻿#if !TOOLS

namespace Robust.Client.UserInterface.XAML.Proxy
{
    /// <summary>
    /// A stub implementation of <see cref="XamlHotReloadManager"/>. Its
    /// behavior is to do nothing.
    /// </summary>
    internal sealed class XamlHotReloadManager : IXamlHotReloadManager
    {
        /// <summary>
        /// Do nothing.
        /// </summary>
        public void Initialize()
        {
        }
    }
}
#endif
