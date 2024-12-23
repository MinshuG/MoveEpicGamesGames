using System;
using Avalonia.Controls;

namespace MoveEpicGamesGames.Utils;

public static class AppHelper
{
    public static Window MainWindow;
    public static TopLevel TopLevel => TopLevel.GetTopLevel(MainWindow) ?? throw new NullReferenceException("TopLevel is null");
}