using Ranks.CustomCombos.Combos;

// Runs the same assertions the plugin runs at Load, without a server.
try
{
    ComboSelfCheck.Run();
    Console.WriteLine("PASS - all assertions held");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL - {ex.Message}");

    return 1;
}
