﻿#if TOOLS
using System;
using System.Reflection;
using Robust.Xaml;

namespace Robust.Client.UserInterface.XAML.Proxy;

/// <summary>
/// This callback has the approximate type of <see cref="XamlJitCompiler.Compile"/>,
/// but it has no error-signaling faculty.
/// </summary>
/// <remarks>
/// Implementors of this delegate should inform the users of errors in their own way.
///
/// Hot reloading failures should not directly take down the process, so implementors
/// should not rethrow exceptions unless they have a strong reason to believe they
/// will be caught.
/// </remarks>
delegate MethodInfo? XamlJitDelegate(Type type, Uri uri, string filename, string content);
#endif
