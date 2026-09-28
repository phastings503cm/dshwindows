using Dsh.App.Model;
using Dsh.App.Views;

namespace Dsh.App;

public sealed class SelfTest
{
    public string Home { get; init; } = "";
    public string? Theme { get; init; }
    public static SelfTest? Parse(string[] args) => null;
    public void Run(MainWindow window, AppModel model) { }
}
