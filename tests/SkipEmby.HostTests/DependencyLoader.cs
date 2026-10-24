using System.Runtime.CompilerServices;
using System.Runtime.Loader;
namespace SkipEmby.HostTests;
internal static class DependencyLoader
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(DependencyLoader).Assembly);
        if (context is not null) context.Resolving += (_, name) => name.Name is "Skip-emby" or "InfuseSync"
            ? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name) : null;
    }
}
