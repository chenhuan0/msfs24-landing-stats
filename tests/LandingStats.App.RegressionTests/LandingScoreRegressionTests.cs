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

        Check(!ideal.Capped, "an ideal contact is not capped");

        var firm = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        firm.InertialFpm = 480;
        var firmScore = Score(firm);
        Near(10, Component(firmScore, LandingScoreCriterion.VerticalSpeed).Score!.Value, 1e-6, "480 fpm (8 ft/s) airliner vertical-speed score");
        Near(100 - 30 * (1 - .1), firmScore.WeightedTotal!.Value, 1e-6, "vertical speed carries 30 points");
        Check(firmScore.Capped && firmScore.Weakest?.Criterion == LandingScoreCriterion.VerticalSpeed, "the weakest criterion caps the total");
        Near(10 + LandingScore.WeakestCriterionMargin, firmScore.Total!.Value, 1e-6, "total is at most the weakest criterion plus the margin");

        // Above Boeing's 4 ft/s crew-report level, with a matching impact.
        var heavyHanded = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        heavyHanded.InertialFpm = 277;
        heavyHanded.PeakG150Milliseconds = 1.31;
        var heavyHandedScore = Score(heavyHanded);
        Near(60 - 40 * 37.0 / 120, Component(heavyHandedScore, LandingScoreCriterion.VerticalSpeed).Score!.Value, 1e-6, "277 fpm scores below the 4 ft/s flag");
        Near(78, Component(heavyHandedScore, LandingScoreCriterion.GForce).Score!.Value, 1e-6, "1.31 G between SMOOTH and the first FDM event");
        Check(heavyHandedScore.Total < 80, "a landing above the hard-landing report level is not graded Good");

        var passMark = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        passMark.BankDegrees = 4;
        var passMarkScore = Score(passMark);
        Near(60, Component(passMarkScore, LandingScoreCriterion.Bank).Score!.Value, 1e-6, "4° bank is CAAC's first flag");
        Check(LandingScoreView.Grade(passMarkScore.Total!.Value) != "Excellent", "one criterion at its pass mark keeps the total below Excellent");

        var light = LandingScore.VerticalSpeedCurve(LandingScoreCategory.Light);
        Near(60, LandingScore.Evaluate(light, 168), 1e-6, "light aircraft scale the 4 ft/s flag to FAR 23.473");
        Near(0, LandingScore.Evaluate(light, 420), 1e-6, "light aircraft reach zero at 7 ft/s");

        var crabbed = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false, headingOffset: 5)));
        Near(5, crabbed.CrabSignedDegrees!.Value, .2, "nose right of the ground track is positive crab");
        Near(20, Component(crabbed, LandingScoreCriterion.Crab).Score!.Value, 5, "5° crab score");

        var gForce = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        gForce.PeakG150Milliseconds = 1.5;
        gForce.PitchDegrees = 1;
        var gForceScore = Score(gForce);
        Near(50, Component(gForceScore, LandingScoreCriterion.GForce).Score!.Value, 1e-6, "1.5 G between the first FDM event and CAAC severe");
        Near(0, Component(gForceScore, LandingScoreCriterion.Pitch).Score!.Value, 1e-6, "1° nose-down airliner pitch");

        var deep = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 700, lateral: .5, reciprocal: false));
        var deepTrack = LandingDistanceRegressionTests.Track(deep);
        var deepDistance = LandingDistanceAnalysis.Create(deep, deepTrack, new[] { 0.0 })!;
        var zone = LandingScore.TouchdownZoneCurve(deepDistance);
        var zoneEnd = deepDistance.TouchdownZoneEndMeters;
        Near(deepDistance.TargetZoneEndMeters, zone[2].X, 1e-6, "full marks span the target zone on a long runway");
        Check(zone[3] == (zoneEnd, 60.0), "touching down at the end of the touchdown zone is the pass mark");
        Check(zone[4] == (zoneEnd + LandingScore.TouchdownZoneSevereMeters, 20.0), "scores fall quickly beyond the touchdown zone");
        Near(100 - 40 * (700 - zone[2].X) / (zoneEnd - zone[2].X), Component(LandingScore.Create(deep, deepTrack, deepDistance),
            LandingScoreCriterion.TouchdownZone).Score!.Value, 1, "700 m sits between the target zone and the pass mark");
        Near(LandingScore.Evaluate(zone, deepDistance.TouchdownMeters),
            Component(LandingScore.Create(deep, deepTrack, deepDistance), LandingScoreCriterion.TouchdownZone).Score!.Value, 1e-6, "deep landing follows the zone curve");

        Check(ideal.BounceCount == 0 && ideal.BounceScore == 100 && ideal.BounceDeduction == 0, "a single contact has no bounce");

        var skip = Score(Bounced(0));
        Check(skip.BounceCount == 1, "one later contact is one bounce");
        Near(92, skip.BounceScore, 1e-6, "a skip without height gain is still a bounce event");
        Near(2, skip.BounceDeduction, 1e-6, "a skip deducts only a little");
        Near(98, skip.Total!.Value, 1e-6, "a skip leaves an ideal landing Excellent");

        var lightBounce = Score(Bounced(2));
        Near(2, lightBounce.BounceHeightsFeet[0]!.Value, .01, "bounce height above the first contact");
        Near(92 - 32 * 2.0 / 5, lightBounce.BounceScore, .05, "the bounce score falls with height up to 5 ft");
        Near(100 - (100 - lightBounce.BounceScore) / 4, lightBounce.Total!.Value, 1e-6, "a quarter of the shortfall is deducted");

        var limitBounce = Score(Bounced(LandingScore.HighBounceFeet));
        Near(60, limitBounce.BounceScore, 1e-3, "a bounce at the FSF 5 ft limit is at the first flag");
        Check(limitBounce.LimitedByBounce, "the bounce sets the cap of an otherwise ideal landing");
        Near(60 + LandingScore.WeakestCriterionMargin, limitBounce.Total!.Value, 1e-3, "a 5 ft bounce keeps the total at Good or below");

        var highBounce = Score(Bounced(6));
        Near(20, highBounce.BounceScore, 1e-6, "a bounce from 6 ft is at the severe level");
        Near(20 + LandingScore.WeakestCriterionMargin, highBounce.Total!.Value, 1e-6, "a high bounce caps the total");

        var repeated = Score(Bounced(1, 2));
        Check(repeated.BounceCount == 2, "each later contact is a bounce");
        Near(1, repeated.BounceHeightsFeet[0]!.Value, .01, "the first bounce is measured on its own");
        Near(2, repeated.BounceHeightsFeet[1]!.Value, .01, "the second bounce is measured on its own");
        Near(Score(Bounced(1)).BounceDeduction + Score(Bounced(2)).BounceDeduction, repeated.BounceDeduction, 1e-6, "each bounce deducts separately");
        Near(100 - (100 - Score(Bounced(1)).BounceScore) - (100 - Score(Bounced(2)).BounceScore), repeated.BounceScore, 1e-6,
            "the bounce shortfalls add up for the cap");

        var firmBounce = Bounced(2);
        firmBounce.InertialFpm = 200;
        var firmBounceScore = Score(firmBounce);
        Near(firmBounceScore.WeightedTotal!.Value - firmBounceScore.BounceDeduction, firmBounceScore.Total!.Value, 1e-6, "the deduction applies below the cap");

        var bankedBounce = Bounced(2);
        bankedBounce.BankDegrees = 4;
        var cappedBounce = Score(bankedBounce);
        Near(60 + LandingScore.WeakestCriterionMargin - cappedBounce.BounceDeduction, cappedBounce.Total!.Value, 1e-6,
            "a bounce still deducts when another criterion already caps the total");

        var unknown = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        unknown.ContactCount = 2;
        var unknownScore = Score(unknown);
        Check(unknownScore.BounceHeightsFeet[0] == null && unknownScore.BounceScore == LandingScore.UnmeasuredBounceScore,
            "a bounce beyond the stored series scores at the light-bounce limit");
        foreach (var clocked in new[] { true, false })
        {
            var measured = Score(unknown, unknown, SecondContact(unknown, heightFeet: 3, gapSeconds: 1, clocked));
            Near(3, measured.BounceHeightsFeet[0]!.Value, .01, $"the contact ending a bounce measures it ({(clocked ? "clocked" : "legacy")})");
        }

        var later = Bounced(2);
        later.ContactNumber = 2;
        var laterView = new LandingScoreView();
        laterView.SetRecord(later);
        Check(laterView.IsBounceContact && laterView.Score == null, "later contacts are recorded as bounces and not scored");
        LandingDistanceRegressionTests.Render(laterView, 1280, 520);

        var longLanding = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 1100, lateral: .5, reciprocal: false)));
        var tdz = Component(longLanding, LandingScoreCriterion.TouchdownZone).Score!.Value;
        Check(tdz < 20 && tdz > 0, "a landing beyond the touchdown zone falls below the severe level");

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
        Near((100 - 30 * (1 - .1) - 25) / 75 * 100, partial.WeightedTotal!.Value, 1e-6, "remaining weights are rescaled to 100");
        Near(10 + LandingScore.WeakestCriterionMargin, partial.Total!.Value, 1e-6, "the cap applies after rescaling");

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

    /// <summary>An ideal first contact that leaves the ground for one second per bounce, every other second from 1 s, peaking at each height.</summary>
    private static LandingRecord Bounced(params double[] heightsFeet)
    {
        var record = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false));
        record.ContactCount = heightsFeet.Length + 1;
        for (var index = 0; index < heightsFeet.Length; index++)
        {
            var start = 1 + 2 * index;
            foreach (var point in record.Series.Where(point => point.TimeSeconds > start && point.TimeSeconds < start + 1))
            {
                var climb = heightsFeet[index] * Math.Sin((point.TimeSeconds - start) * Math.PI);
                point.OnGround = false;
                point.AglFeet += climb;
                point.PlaneAltitudeFeet += climb;
            }
        }
        return record;
    }

    /// <summary>The second contact of <paramref name="first"/>, airborne for <paramref name="gapSeconds"/> before it and peaking at <paramref name="heightFeet"/>.</summary>
    private static LandingRecord SecondContact(LandingRecord first, double heightFeet, double gapSeconds, bool clocked)
    {
        var record = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 550, lateral: .5, reciprocal: false));
        record.ContactNumber = 2;
        record.ContactCount = first.ContactCount;
        first.EstimatedContactTimeSeconds = clocked ? 100 : null;
        record.EstimatedContactTimeSeconds = clocked ? 100 + gapSeconds : null;
        var groundAgl = record.Series.First(point => point.TimeSeconds >= 0).AglFeet;
        foreach (var point in record.Series.Where(point => point.TimeSeconds > -gapSeconds - 1 && point.TimeSeconds < 0))
        {
            // Before the bounce the aircraft rolls on the first contact.
            var airborne = point.TimeSeconds > -gapSeconds;
            point.OnGround = !airborne;
            point.AglFeet = groundAgl + (airborne ? heightFeet * Math.Sin(-point.TimeSeconds / gapSeconds * Math.PI) : 0);
        }
        return record;
    }

    private static LandingScore Score(LandingRecord record, params LandingRecord[] episode)
    {
        var track = LandingDistanceRegressionTests.Track(record);
        return LandingScore.Create(record, track, LandingDistanceAnalysis.Create(record, track, new[] { 0.0 }), episode);
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
