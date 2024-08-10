namespace Robust.Client.UserInterface.XAML.JIT
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
    public sealed class XamlJitEmbeddedResourceAttribute: System.Attribute
    {
        public readonly string ResourceName;
        public readonly string Uri;

        public XamlJitEmbeddedResourceAttribute(string resourceName, string uri)
        {
            ResourceName = resourceName;
            Uri = uri;
        }

    }
}
