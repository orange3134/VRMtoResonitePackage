internal static class ParserChecks
{
    public static void Run(string artifacts)
    {
        CacExpressionChecks.Run(artifacts);
        NormalExpressionPatternChecks.Run(artifacts);
        IndirectFaceEmoChecks.Run(artifacts);
        FaceExpressionDetectionChecks.Run(artifacts);
        MixedExpressionClipChecks.Run(artifacts);
        WeightedCurveChecks.Run();
    }
}
