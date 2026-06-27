
namespace ChatfishApp.Shared.Services;

public static class AppEnvironment
{
    public static bool IsMaui { get; private set; }

    public static void SetMaui() => IsMaui = true;
}