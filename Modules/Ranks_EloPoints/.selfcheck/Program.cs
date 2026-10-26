using Ranks.EloPoints.Rating;

// Throwaway harness: runs the module's load-time self-check without a server or the Ranks plugin.
try
{
    EloMathSelfCheck.Run();
    Console.WriteLine("PASS - all assertions held");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL - " + ex.Message);
    return 1;
}
