internal static class ParserChecks
{
    public static void Run(string artifacts)
    {
        CacExpressionChecks.Run(artifacts);
        NormalExpressionPatternChecks.Run(artifacts);
        FaceExpressionDetectionChecks.Run(artifacts);
        MixedExpressionClipChecks.Run(artifacts);
        WeightedCurveChecks.Run();
    }
}