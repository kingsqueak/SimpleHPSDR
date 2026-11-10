namespace SimpleHPSDR.Engine;

// The engine's /api/dsp/imd-measure shapes (Station.Engine.Hosting.ImdMeasureService),
// which aren't in SimpleHPSDR.Contracts. Dbc is how far the products sit below the
// weaker tone, positive.
public sealed record ImdRequest(double?[] Db, double Width, double CenterHz, double HzPerPixel, double? ExpectedToneSpacingHz);
public sealed record ImdProductReading(double LowerDbm, double UpperDbm, double Dbc, double LowerHz, double UpperHz);
public sealed record ImdReading(bool Ok, string? Reason, ImdProductReading? Imd3, ImdProductReading? Imd5, double? Oip3);
