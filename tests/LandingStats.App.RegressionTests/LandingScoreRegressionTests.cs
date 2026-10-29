using System;
using System.IO;
using System.Linq;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;

namespace LandingStats.App.RegressionTests;

/// <summary>Expected values follow the TouchdownFX 2.1.7 scorecard formula.</summary>
internal static class LandingScoreRegressionTests
{
    public static void RunAll()
    {
        var ideal = Score(Airliner(Ideal()));
        Check(ideal.Category == LandingScoreCategory.Airliner, "contact airspeed selects the airliner category");
        Check(ideal.Components.Count == 7 && Math.Abs(ideal.Components.Sum(component => component.Weight) - 100) < 1e-9, "seven criteria share 100 points");
        Check(ideal.Components.All(component => component.Score == 100 && component.Points == component.Weight),
            "an on-target, aligned, gentle contact scores every criterion fully");
        Check(ideal.Total == 100 && ideal.Penalties.Count == 0, "ideal total without penalties");
        Near(0, ideal.CrabSignedDegrees!.Value, .2, "no crab when the nose follows the ground track");

        var firm = Airliner(Ideal());
        firm.InertialFpm = 277;
        firm.PeakG150Milliseconds = 1.31;
        var firmScore = Score(firm);
        Near(100 * (1 - 97.0 / 420), Component(firmScore, LandingScoreCriterion.VerticalSpeed).Score!.Value, 1e-6,
            "airliner vertical speed falls linearly from 180 to 600 fpm");
        Near(100 * (1 - .11 / .3), Component(firmScore, LandingScoreCriterion.GForce).Score!.Value, 1e-6, "peak G falls linearly from 1.2 to 1.5 G");
        Check(Component(firmScore, LandingScoreCriterion.VerticalSpeed).Points == 23 && Component(firmScore, LandingScoreCriterion.GForce).Points == 9,
            "points are rounded down");
        Check(firmScore.Total == 87, "the total sums the whole points");

        foreach (var (airspeed, expected) in new[]
        {
            (60.0, LandingScoreCategory.Light), (70.0, LandingScoreCategory.Turboprop), (109.9, LandingScoreCategory.Turboprop),
            (110.0, LandingScoreCategory.Airliner), (155.0, LandingScoreCategory.Heavy), (0.0, LandingScoreCategory.Airliner),
            (double.NaN, LandingScoreCategory.Airliner),
        })
            Check(LandingScore.CategoryFor(airspeed) == expected, $"{airspeed} kt is {expected}");
        var heavyWeight = Airliner(Ideal());
        heavyWeight.WeightPounds = 400000;
        Check(Score(heavyWeight).Category == LandingScoreCategory.Airliner, "landing weight does not change the category");
        Near(50, LandingScore.Evaluate(LandingScore.VerticalSpeedCurve(LandingScoreCategory.Light), 240), 1e-6, "light: full to 80, zero at 400 fpm");
        Near(0, LandingScore.Evaluate(LandingScore.VerticalSpeedCurve(LandingScoreCategory.Turboprop), 500), 1e-6, "turboprop: zero at 500 fpm");
        Near(100, LandingScore.Evaluate(LandingScore.VerticalSpeedCurve(LandingScoreCategory.Heavy), 240), 1e-6, "heavy: full to 240 fpm");

        var hard = Airliner(Ideal());
        hard.PeakG150Milliseconds = 2.0;
        var hardScore = Score(hard);
        Check(Component(hardScore, LandingScoreCriterion.GForce).Score == 0, "2 G scores no peak-G points");
        Check(hardScore.Penalties.Single().Kind == LandingScorePenaltyKind.GForce, "peak G above 1.5 G is penalised");
        Near(.7, hardScore.Multiplier, 1e-9, "the G penalty falls from 1 at 1.5 G to 0.4 at 2.5 G");
        Check(hardScore.Total == Math.Floor(85 * .7), "the penalty multiplies the base score");

        var deep = Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 700, lateral: .5, reciprocal: false));
        var deepTrack = LandingDistanceRegressionTests.Track(deep);
        var deepDistance = LandingDistanceAnalysis.Create(deep, deepTrack, new[] { 0.0 })!;
        Check(LandingScore.TouchdownZoneCurve(deepDistance, LandingScoreCategory.Airliner)
                .SequenceEqual(new[] { (0.0, 0.0), (300.0, 100.0), (500.0, 100.0), (900.0, 0.0) }),
            "3,000 m runway: aiming point 400 m, full within 100 m, zero 500 m past it");
        Check(LandingScore.TouchdownZoneCurve(deepDistance, LandingScoreCategory.Light)
                .SequenceEqual(new[] { (0.0, 0.0), (250.0, 100.0), (550.0, 100.0), (800.0, 0.0) }),
            "light aircraft get 150 m either side and reach zero 400 m past the aiming point");
        Near(50, Component(LandingScore.Create(deep, deepTrack, deepDistance), LandingScoreCriterion.TouchdownZone).Score!.Value, 1,
            "700 m is halfway down the decline");
        var displacedRunway = deep.RunwayGeometry!;
        displacedRunway.PrimaryThresholdOffsetMeters = 800;
        var displaced = LandingDistanceAnalysis.ForTouchdown(displacedRunway, false, 300, 0)!;
        Check(LandingScore.TouchdownZoneCurve(displaced, LandingScoreCategory.Airliner)
                .SequenceEqual(new[] { (0.0, 0.0), (200.0, 100.0), (400.0, 100.0), (700.0, 0.0) }),
            "a 2,200 m landing distance aims at 300 m and the runway midpoint ends the zone");
        displacedRunway.PrimaryThresholdOffsetMeters = 0;
        displacedRunway.LengthMeters = 1000;
        var shortRunway = LandingDistanceAnalysis.ForTouchdown(displacedRunway, false, 300, 0)!;
        Check(LandingScore.TouchdownZoneCurve(shortRunway, LandingScoreCategory.Airliner)
                .SequenceEqual(new[] { (0.0, 0.0), (150.0, 100.0), (350.0, 100.0), (500.0, 0.0) }),
            "a 1,000 m runway aims at 250 m");

        var late = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 2100, lateral: .5, reciprocal: false)));
        Check(Component(late, LandingScoreCriterion.TouchdownZone).Points == 0, "a late touchdown scores no touchdown-zone points");
        Check(late.Penalties.Single().Kind == LandingScorePenaltyKind.LateTouchdown, "a touchdown past 60% of the runway is penalised");
        Near(.7, late.Multiplier, .01, "70% of the runway is halfway to the 0.4 floor at 80%");

        Near(100 * (1 - 6.8 / 19), LandingScore.Evaluate(LandingScore.CenterlineCurve, 7.8), 1e-6, "centreline: full to 1 m, zero at 20 m");
        var wide = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: 22, reciprocal: false)));
        Check(Component(wide, LandingScoreCriterion.Centerline).Score == 0, "22 m off the centreline scores nothing");
        Check(wide.Penalties.Single().Kind == LandingScorePenaltyKind.Centerline, "more than 20 m off the centreline is penalised");
        Near(.95, wide.Multiplier, .01, "the centreline penalty falls to 0.5 at 40 m");

        var crabbed = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false, headingOffset: 2.75)));
        Near(2.75, crabbed.CrabSignedDegrees!.Value, .2, "nose right of the ground track is positive crab");
        Near(50, Component(crabbed, LandingScoreCriterion.Crab).Score!.Value, 5, "crab: full to 0.5°, zero at 5°");
        var skidded = Score(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false, headingOffset: 8)));
        Check(skidded.Penalties.Single().Kind == LandingScorePenaltyKind.Crab, "crab above 5° is penalised");
        Near(.82, skidded.Multiplier, .02, "the crab penalty falls to 0.4 at 15°");

        Near(50, Component(Score(Banked(3)), LandingScoreCriterion.Bank).Score!.Value, 1e-6, "bank: full to 1°, zero at 5°");
        Check(Score(Banked(-3, windKnots: 10)).Components.Single(component => component.Criterion == LandingScoreCriterion.Bank).Score == 100,
            "banking right into a 10 kt crosswind from the right is allowed 3°");
        Near(50, Component(Score(Banked(3, windKnots: 10)), LandingScoreCriterion.Bank).Score!.Value, 1e-6, "banking away from the crosswind is not");
        Near(50, Component(Score(Banked(-3, windKnots: 4)), LandingScoreCriterion.Bank).Score!.Value, 1e-6, "a 4 kt crosswind earns no allowance");
        var steep = Score(Banked(7));
        Check(steep.Penalties.Single().Kind == LandingScorePenaltyKind.Bank && Math.Abs(steep.Multiplier - .8) < 1e-9 && steep.Total == 72,
            "bank above 5° is penalised down to 0.5 at 10°");

        var pitch = LandingScore.PitchCurve(LandingScoreCategory.Airliner);
        Near(100, LandingScore.Evaluate(pitch, 7), 1e-6, "airliner pitch: full from 3° to 7°");
        Near(100 - 100 / 6.0, LandingScore.Evaluate(pitch, 8), 1e-6, "a sixth per degree above the band");
        Near(62.5, LandingScore.Evaluate(pitch, 0), 1e-6, "an eighth per degree below 3°");
        Near(100 - 100 / 6.0, LandingScore.Evaluate(LandingScore.PitchCurve(LandingScoreCategory.Light), 6), 1e-6, "light pitch band ends at 5°");
        var noseDown = Airliner(Ideal());
        noseDown.PitchDegrees = 1;
        Near(50, Component(Score(noseDown), LandingScoreCriterion.Pitch).Score!.Value, 1e-6, "1° nose down");

        var noRunway = Airliner(Ideal());
        noRunway.RunwayGeometry = null;
        var partial = LandingScore.Create(noRunway, LandingDistanceRegressionTests.Track(noRunway), null);
        Check(Component(partial, LandingScoreCriterion.TouchdownZone) is { Value: null, Points: 0 } &&
              Component(partial, LandingScoreCriterion.Centerline) is { Value: null, Points: 10 },
            "without a runway, TouchdownFX awards no touchdown-zone points and full centreline points");
        Check(partial.Total == 85, "the other criteria still count");
        var noVerticalSpeed = Airliner(Ideal());
        noVerticalSpeed.InertialFpm = double.NaN;
        Check(Score(noVerticalSpeed).Total == null, "no score without a vertical speed");

        Check(Score(Bounced(6)).Total == ideal.Total, "bounces do not change the score of the first contact");
        var later = Bounced(2);
        later.ContactNumber = 2;
        var laterView = new LandingScoreView();
        laterView.SetRecord(later);
        Check(laterView.IsBounceContact && laterView.Score == null, "later contacts are recorded as bounces and not scored");
        LandingDistanceRegressionTests.Render(laterView, 1280, 520);

        foreach (var (total, grade) in new[]
        {
            (100.0, "Excellent"), (90.0, "Excellent"), (89.0, "Good"), (75.0, "Good"), (74.0, "Acceptable"), (60.0, "Acceptable"),
            (59.0, "BelowAverage"), (40.0, "BelowAverage"), (39.0, "Poor"), (20.0, "Poor"), (19.0, "Dangerous"), (0.0, "Dangerous"),
        })
            Check(LandingScoreView.Grade(total) == grade, $"{total} is {grade}");

        var view = new LandingScoreView();
        view.SetRecord(Airliner(LandingDistanceRegressionTests.Record("09", touchdown: 2100, lateral: 3, reciprocal: false, headingOffset: 7)));
        Check(view.Score?.Total != null && view.Score.Penalties.Count == 2, "view prepares the score with its penalties");
        var bitmap = LandingDistanceRegressionTests.Render(view, 1280, 520);
        var preview = Environment.GetEnvironmentVariable("MSFSLS_DISTANCE_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview)) LandingDistanceRegressionTests.Save(bitmap, Path.Combine(preview, "score-synthetic.png"));
        var empty = new LandingScoreView();
        empty.SetRecord(new LandingRecord());
        LandingDistanceRegressionTests.Render(empty, 800, 400);
    }

    private static LandingRecord Ideal() => LandingDistanceRegressionTests.Record("09", touchdown: 450, lateral: .5, reciprocal: false);

    /// <summary>An ideal contact on runway 09 with the given MSFS bank and an optional wind from the south, the right of the landing direction.</summary>
    private static LandingRecord Banked(double bankDegrees, double windKnots = 0)
    {
        var record = Airliner(Ideal());
        record.BankDegrees = bankDegrees;
        record.WindSpeedKnotsAtContact = windKnots;
        record.WindDirectionDegreesAtContact = 180;
        return record;
    }

    internal static LandingRecord Airliner(LandingRecord record)
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
    internal static LandingRecord Bounced(params double[] heightsFeet)
    {
        var record = Airliner(Ideal());
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
    internal static LandingRecord SecondContact(LandingRecord first, double heightFeet, double gapSeconds, bool clocked)
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
