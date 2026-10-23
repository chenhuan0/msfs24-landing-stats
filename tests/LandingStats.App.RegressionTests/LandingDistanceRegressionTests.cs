using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;
using LandingStats.App.Storage;

namespace LandingStats.App.RegressionTests;

internal static class LandingDistanceRegressionTests
{
    private const double KnotsToMetersPerSecond = 0.514444;
    private const double RunwayLatitude = 40, RunwayLongitude = -75, RunwayAltitudeMeters = 100, GearHeightMeters = 3;

    public static void RunAll()
    {
        // 3° straight-in to a 450 m touchdown, 2 m right of the centreline,
        // then 2 m/s² deceleration from 140 kt.
        var primary = Analyse(Record("09", touchdown: 450, lateral: 2, reciprocal: false));
        Near(450, primary.TouchdownMeters, 2, "primary touchdown distance");
        Near(2, primary.TouchdownLateralMeters, .3, "right of the landing direction is positive");
        Check(primary.AimSource == LandingAimSource.Nominal && Math.Abs(primary.AimingPointMeters - 310.896) < .01, "FAA nominal aiming point");
        Near(914.4, primary.TouchdownZoneEndMeters, .01, "FAA touchdown zone");
        Near(310.896, primary.TargetZoneStartMeters, .01, "target zone starts at the aiming point");
        Near(615.696, primary.TargetZoneEndMeters, .01, "target zone spans 1,000 ft");
        Check(primary.Zone == LandingDistanceZone.Target, "450 m is inside the target zone");
        Near(450 * Math.Tan(3 * Math.PI / 180) / .3048, primary.ThresholdCrossingHeightFeet!.Value, 1.5, "threshold crossing height");
        Near(15.24 / Math.Tan(3 * Math.PI / 180), primary.AirDistanceMeters!.Value, 3, "air distance from 50 ft");
        Near(2550, primary.RunwayRemainingMeters, 2, "runway remaining");
        var exitTime = (72 - 30 * KnotsToMetersPerSecond) / 2;
        var exitAlong = 450 + 72 * exitTime - exitTime * exitTime;
        Near(exitAlong, primary.ExitSpeedMeters!.Value, 10, "30 kt point");
        Check(!primary.ExitSpeedProjected, "a recorded 30 kt point is not projected");
        // A stored 10 s rollout continues its constant deceleration to the same point.
        var truncated = Analyse(Record("09", touchdown: 450, lateral: 2, reciprocal: false, rolloutSeconds: 10));
        Check(truncated.ExitSpeedProjected, "a short rollout projects the 30 kt point");
        Near(2, truncated.ProjectedDecelerationMetersPerSecondSquared!.Value, .05, "recorded deceleration");
        Near(exitAlong, truncated.ExitSpeedMeters!.Value, 10, "projected 30 kt point");
        Check(primary.FlareStartMeters.HasValue && primary.FlareStartMeters < primary.TouchdownMeters, "flare is before the touchdown");
        Check(primary.Contacts.Count == 1 && primary.Contacts[0].Selected && primary.Contacts[0].Number == 1, "selected contact marker");

        var reciprocal = Analyse(Record("27", touchdown: 450, lateral: 2, reciprocal: true));
        Near(450, reciprocal.TouchdownMeters, 2, "reciprocal touchdown distance");
        Near(2, reciprocal.TouchdownLateralMeters, .3, "reciprocal lateral sign follows the landing direction");

        var displaced = Record("09", touchdown: 450, lateral: 0, reciprocal: false);
        displaced.RunwayGeometry!.PrimaryThresholdOffsetMeters = 200;
        var shifted = Analyse(displaced);
        Near(250, shifted.TouchdownMeters, 2, "distance is measured from the displaced threshold");
        Near(2550, shifted.RunwayRemainingMeters, 2, "displacement does not change the remaining pavement");
        Check(shifted.ThresholdOffsetKnown && shifted.Zone == LandingDistanceZone.ShortOfTarget, "displaced touchdown is short of the target");

        var long_ = Analyse(Record("09", touchdown: 1100, lateral: 0, reciprocal: false));
        Check(long_.Zone == LandingDistanceZone.BeyondTouchdownZone, "1,100 m is beyond the touchdown zone");

        var ils = Record("09", touchdown: 450, lateral: 0, reciprocal: false);
        Offset(300 - 1500, -120, out var ilsLatitude, out var ilsLongitude);
        ils.RunwayGeometry!.PrimaryIlsGlideSlope = new RunwayGlideSlope
        {
            Ident = "ITST", Region = "K6", LatitudeDegrees = ilsLatitude, LongitudeDegrees = ilsLongitude,
            AltitudeMeters = RunwayAltitudeMeters, AngleDegrees = 3.2,
        };
        var guided = Analyse(ils);
        Check(guided.AimSource == LandingAimSource.Ils, "ILS sets the aiming point");
        // The shared marking model offsets the antenna with a spherical Earth (~0.25% here).
        Near(300, guided.AimingPointMeters, 5, "ILS glide-path intersection");
        Near(3.2, guided.GlidePathDegrees, 1e-9, "ILS glide-path angle");

        var noRunway = Record("09", touchdown: 450, lateral: 0, reciprocal: false);
        noRunway.RunwayGeometry = null;
        Check(LandingDistanceAnalysis.Create(noRunway, Track(noRunway), new[] { 0.0 }) == null, "no runway geometry means no analysis");

        // The view must lay out, render and follow an external hover time.
        var view = new LandingDistanceView();
        view.SetRecord(Record("09", touchdown: 450, lateral: 2, reciprocal: false));
        Check(view.Analysis != null, "view prepares the analysis");
        view.SetHoverTime(-3);
        var bitmap = Render(view, 1280, 600);
        var preview = Environment.GetEnvironmentVariable("MSFSLS_DISTANCE_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview)) Save(bitmap, Path.Combine(preview, "synthetic.png"));
        var empty = new LandingDistanceView();
        empty.SetRecord(new LandingRecord());
        Check(empty.Analysis == null, "a record without positions has no analysis");
        Render(empty, 800, 400);
    }

    /// <summary>Read-only preview of real history files: summary CSV plus PNGs of the newest records.</summary>
    public static int Preview(string landingsDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var records = LoadRecords(landingsDirectory);
        var lines = new List<string> { "id,utc,airport,runway,contact,source,td_m,lateral_m,zone,aim_m,tch_ft,air_m,remaining_m,exit_remaining_m,threshold_known,end_s,end_m,end_gs_kt,category,score,vs,g,tdz,centerline,crab,bank,pitch,crab_deg,wind_kt,vs_v,g_v,tdz_v,centerline_v,crab_v,bank_v,pitch_v,bounces,bounce_ft,bounce_score,uncapped" };
        var rendered = 0;
        foreach (var record in records.OrderByDescending(record => record.TimestampUtc))
        {
            var view = new LandingDistanceView();
            view.SetRecord(record, records);
            var analysis = view.Analysis;
            if (analysis == null) continue;
            var scoreView = new LandingScoreView();
            scoreView.SetRecord(record, records);
            // Later contacts of a bounced landing are not scored.
            var score = scoreView.Score;
            var blank = string.Join(",", Enumerable.Repeat(string.Empty, 7));
            lines.Add(string.Join(",", record.Id, record.TimestampUtc.ToString("s", CultureInfo.InvariantCulture), analysis.AirportIdent, analysis.RunwayName,
                $"{record.ContactNumber}/{record.ContactCount}", analysis.AimSource, F(analysis.TouchdownMeters), F(analysis.TouchdownLateralMeters, "0.0"),
                analysis.Zone, F(analysis.AimingPointMeters), F(analysis.ThresholdCrossingHeightFeet), F(analysis.AirDistanceMeters),
                F(analysis.RunwayRemainingMeters), F(analysis.ExitSpeedRemainingMeters), analysis.ThresholdOffsetKnown,
                F(analysis.Samples[analysis.Samples.Count - 1].TimeSeconds, "0.0"), F(analysis.Samples[analysis.Samples.Count - 1].AlongMeters),
                F(analysis.Samples[analysis.Samples.Count - 1].GroundSpeedKnots), score?.Category.ToString() ?? "bounce", F(score?.Total),
                score == null ? blank : string.Join(",", score.Components.Select(component => F(component.Score))), F(score?.CrabSignedDegrees, "0.00"),
                F(record.WindSpeedKnotsAtContact, "0.0"), score == null ? blank : string.Join(",", score.Components.Select(component => F(component.Value, "0.00"))),
                score?.BounceCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty, score == null ? string.Empty : string.Join(";", score.BounceHeightsFeet.Select(height => F(height, "0.00"))), F(score?.BounceScore),
                F(score?.UncappedTotal)));
            if (rendered++ < 12)
            {
                view.SetHoverTime(-2);
                var name = $"{rendered:00}-{analysis.AirportIdent}-{analysis.RunwayName}-{record.Id.Substring(0, 8)}.png";
                Save(Render(view, 1280, 600), Path.Combine(outputDirectory, name));
                if (rendered <= 4) Save(Render(scoreView, 1280, 520), Path.Combine(outputDirectory, "score-" + name));
            }
        }
        File.WriteAllLines(Path.Combine(outputDirectory, "summary.csv"), lines);
        Console.WriteLine($"records {records.Count}, analysed {lines.Count - 1}, rendered {Math.Min(rendered, 12)}");
        return 0;

        static string F(double? value, string format = "0") =>
            value is double number ? number.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>Read-only timing of loading real history files and scoring every first contact, single-threaded.</summary>
    public static int ScoreTiming(string landingsDirectory)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var records = LoadRecords(landingsDirectory);
        var loaded = clock.Elapsed;
        clock.Restart();
        var scored = records.Where(record => record.ContactNumber == 1)
            .Select(record => LandingScore.ForFirstContact(record, records)?.Total).Count(total => total.HasValue);
        Console.WriteLine($"records {records.Count}: load {loaded.TotalSeconds:0.00} s ({loaded.TotalMilliseconds / Math.Max(1, records.Count):0.0} ms each), " +
            $"score {clock.Elapsed.TotalSeconds:0.00} s ({clock.Elapsed.TotalMilliseconds / Math.Max(1, records.Count):0.0} ms each), {scored} scored");
        return 0;
    }

    private static List<LandingRecord> LoadRecords(string landingsDirectory)
    {
        var serializer = new DataContractJsonSerializer(typeof(LandingRecordFile));
        var records = new List<LandingRecord>();
        foreach (var path in Directory.EnumerateFiles(landingsDirectory, "*.landing.json.gz"))
        {
            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                if (serializer.ReadObject(gzip) is LandingRecordFile payload) records.Add(payload.ToRecord());
            }
            catch (Exception exception) when (exception is IOException || exception is System.Runtime.Serialization.SerializationException || exception is InvalidDataException)
            { Console.WriteLine($"skip {Path.GetFileName(path)}: {exception.GetType().Name}"); }
        }
        return records;
    }

    private static LandingDistanceAnalysis Analyse(LandingRecord record) =>
        LandingDistanceAnalysis.Create(record, Track(record), new[] { 0.0 }) ?? throw new InvalidOperationException("Distance analysis is unavailable.");

    internal static LandingReplayTrack Track(LandingRecord record) =>
        LandingReplayTrack.TryCreate(record, out var track) ? track! : throw new InvalidOperationException("Replay track is unavailable.");

    /// <summary>3° straight-in at 140 kt to <paramref name="touchdown"/> m past the threshold of a 3,000 m FAA runway.</summary>
    internal static LandingRecord Record(string runway, double touchdown, double lateral, bool reciprocal, double rolloutSeconds = 30, double headingOffset = 0)
    {
        var record = new LandingRecord
        {
            Airport = "KTST", Runway = runway, FlareStartSeconds = -6,
            RunwayGeometry = new RunwayFacility
            {
                AirportIdent = "KTST", AirportRegion = "K6", LatitudeDegrees = RunwayLatitude, LongitudeDegrees = RunwayLongitude,
                AltitudeMeters = RunwayAltitudeMeters, HeadingTrueDegrees = 90, LengthMeters = 3000, WidthMeters = 45,
                PrimaryName = "09", SecondaryName = "27", PrimaryThresholdOffsetMeters = 0, SecondaryThresholdOffsetMeters = 0,
            },
        };
        const double approachSpeed = 72;
        var groundFeet = RunwayAltitudeMeters / .3048;
        for (var step = -400; step <= rolloutSeconds * 10; step++)
        {
            var time = step / 10.0;
            var along = time <= 0 ? touchdown + approachSpeed * time : touchdown + approachSpeed * time - time * time;
            var speed = time <= 0 ? approachSpeed : Math.Max(0, approachSpeed - 2 * time);
            var height = time <= 0 ? (touchdown - along) * Math.Tan(3 * Math.PI / 180) : 0;
            // Primary lands east from the west threshold; the reciprocal mirrors it.
            var east = reciprocal ? 1500 - along : along - 1500;
            var north = reciprocal ? lateral : -lateral;
            Offset(east, north, out var latitude, out var longitude);
            record.Series.Add(new LandingSeriesPoint
            {
                TimeSeconds = time, LatitudeDegrees = latitude, LongitudeDegrees = longitude,
                GroundAltitudeFeet = groundFeet, PlaneAltitudeFeet = groundFeet + (GearHeightMeters + height) / .3048,
                HeadingTrueDegrees = (reciprocal ? 270 : 90) + headingOffset, GroundSpeedKnots = speed / KnotsToMetersPerSecond,
                OnGround = time >= 0, AglFeet = (GearHeightMeters + height) / .3048,
            });
        }
        return record;
    }

    private static void Offset(double east, double north, out double latitude, out double longitude)
    {
        // WGS 84 local radii keep the synthetic positions consistent with the replay projection.
        const double radius = 6378137, eccentricitySquared = 0.0066943799901413165;
        var phi = RunwayLatitude * Math.PI / 180;
        var denominator = 1 - eccentricitySquared * Math.Sin(phi) * Math.Sin(phi);
        var meridian = radius * (1 - eccentricitySquared) / Math.Pow(denominator, 1.5);
        var prime = radius / Math.Sqrt(denominator);
        latitude = RunwayLatitude + north / meridian * 180 / Math.PI;
        longitude = RunwayLongitude + east / (prime * Math.Cos(phi)) * 180 / Math.PI;
    }

    internal static RenderTargetBitmap Render(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0D, 0x0C, 0x0B)), null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(view);
        return bitmap;
    }

    internal static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

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
