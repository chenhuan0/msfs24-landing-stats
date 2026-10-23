using System;
using System.IO;
using System.Linq;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;

namespace LandingStats.App.RegressionTests;

internal static class LandingScoreRegressionTests
{
    public static void RunAll()
    {
        var ideal = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false)));
        Check(ideal.Category == LandingScoreCategory.Airliner && ideal.CategoryFromWeight, "landing weight selects the airliner category");
        Check(ideal.Components.Count == 7 && Math.Abs(ideal.Components.Sum(component => component.Weight) - 100) < 1e-9, "seven criteria share 100 points");
        Check(ideal.Components.All(component => component.Score == 100), "an on-target, aligned, gentle contact scores every criterion fully");
        Near(100, ideal.Total!.Value, 1e-6, "ideal total");
        Near(0, ideal.CrabSignedDegrees!.Value, .2, "no crab when the nose follows the ground track");

        var firm = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        firm.InertialFpm = 480;
        var firmScore = Score(firm);
        Near(37.5, Component(firmScore, LandingScoreCriterion.VerticalSpeed).Score!.Value, 1e-6, "480 fpm airliner vertical-speed score");
        Near(100 - 30 * (1 - .375), firmScore.Total!.Value, 1e-6, "vertical speed carries 30 points");

        var crabbed = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false, headingOffset: 5)));
        Near(5, crabbed.CrabSignedDegrees!.Value, .2, "nose right of the ground track is positive crab");
        Near(40, Component(crabbed, LandingScoreCriterion.Crab).Score!.Value, 2, "5° crab score");

        var gForce = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        gForce.PeakG150Milliseconds = 1.5;
        gForce.PitchDegrees = 1;
        var gForceScore = Score(gForce);
        Near(60, Component(gForceScore, LandingScoreCriterion.GForce).Score!.Value, 1e-6, "FIRM limit scores 60");
        Near(25, Component(gForceScore, LandingScoreCriterion.Pitch).Score!.Value, 1e-6, "1° nose-down airliner pitch");

        var longLanding = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 1100, lateral: .5, reciprocal: false)));
        var tdz = Component(longLanding, LandingScoreCriterion.TouchdownZone).Score!.Value;
        Check(tdz < 50 && tdz > 0, "a landing beyond the touchdown zone falls below half marks");

        foreach (var (weight, airspeed, expected, fromWeight) in new[]
        {
            (2500.0, 70.0, LandingScoreCategory.Light, true), (50000.0, 110.0, LandingScoreCategory.Regional, true),
            (400000.0, 150.0, LandingScoreCategory.Heavy, true), (0.0, 70.0, LandingScoreCategory.Light, false),
            (0.0, 160.0, LandingScoreCategory.Heavy, false),
        })
        {
            var record = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
            record.WeightPounds = weight; record.AirspeedKnots = airspeed;
            var category = Score(record);
            Check(category.Category == expected && category.CategoryFromWeight == fromWeight, $"category {expected} from {(fromWeight ? "weight" : "airspeed")}");
        }

        var noRunway = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        noRunway.RunwayGeometry = null;
        noRunway.InertialFpm = 480;
        var partial = LandingScore.Create(noRunway, LandingDistanceRegressionTests.Track(noRunway), null);
        Check(Component(partial, LandingScoreCriterion.TouchdownZone).Score == null &&
              Component(partial, LandingScoreCriterion.Centerline).Score == null, "runway criteria are unavailable without geometry");
        Near(75, partial.AvailableWeight, 1e-9, "available weight excludes runway criteria");
        Near((100 - 30 * (1 - .375) - 25) / 75 * 100, partial.Total!.Value, 1e-6, "remaining weights are rescaled to 100");

        var view = new LandingScoreView();
        view.SetRecord(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 700, lateral: 3, reciprocal: false, headingOffset: 2)));
        Check(view.Score?.Total != null, "view prepares the score");
        var bitmap = LandingDistanceRegressionTests.Render(view, 1280, 520);
        var preview = Environment.GetEnvironmentVariable("MSFSLS_DISTANCE_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview)) LandingDistanceRegressionTests.Save(bitmap, Path.Combine(preview, "score-synthetic.png"));
        var empty = new LandingScoreView();
        empty.SetRecord(new LandingRecord());
        LandingDistanceRegressionTests.Render(empty, 800, 400);
    }

    private static LandingRecord Airliner(LandingRecord record)
    {
        record.InertialFpm = 120;
        record.PeakG150Milliseconds = 1.1;
        record.PitchDegrees = -4;
        record.BankDegrees = .5;
        record.WeightPounds = 140000;
        record.AirspeedKnots = 140;
        return record;
    }

    private static LandingScore Score(LandingRecord record)
    {
        var track = LandingDistanceRegressionTests.Track(record);
        return LandingScore.Create(record, track, LandingDistanceAnalysis.Create(record, track, new[] { 0.0 }));
    }

    private static LandingScoreComponent Component(LandingScore score, LandingScoreCriterion criterion) =>
        score.Components.Single(component => component.Criterion == criterion);

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected:0.###} ± {tolerance}, actual {actual:0.###}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
