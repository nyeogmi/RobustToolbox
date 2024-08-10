using System;

namespace Robust.Client.UserInterface.XAML
{
    public static class RobustXamlLoader
    {
        public static void Load(object obj)
        {
            // TODO: Just trapdoor this?
            throw new Exception(
                $"No embedded XAML found for {obj.GetType()}, make sure to specify Class or name your class the same as your .xaml ");
        }
    }
}
