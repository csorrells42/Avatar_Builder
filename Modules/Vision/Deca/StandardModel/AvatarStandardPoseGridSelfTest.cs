using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public static class AvatarStandardPoseGridSelfTest
{
	public static AvatarStandardPoseGridSelfTestResult Run(string outputFolder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder, "outputFolder");
		Directory.CreateDirectory(outputFolder);
		try
		{
			(double, double, string)[] array = new(double, double, string)[9]
			{
				(-20.0, -20.0, "A-/B-"),
				(-20.0, 0.0, "A-/B0"),
				(-20.0, 20.0, "A-/B+"),
				(0.0, -20.0, "A0/B-"),
				(0.0, 0.0, "A0/B0"),
				(0.0, 20.0, "A0/B+"),
				(20.0, -20.0, "A+/B-"),
				(20.0, 0.0, "A+/B0"),
				(20.0, 20.0, "A+/B+")
			};
			List<FaceMeshLandmarkPoint> mediaPipeLandmarks = (from index in Enumerable.Range(0, 478)
				select new FaceMeshLandmarkPoint
				{
					Index = index,
					X = (double)index / 478.0,
					Y = (double)(477 - index) / 478.0,
					Z = (double)index * 0.0001
				}).ToList();
			Dictionary<string, AvatarStandardPoseSample> dictionary = new Dictionary<string, AvatarStandardPoseSample>(StringComparer.Ordinal);
			string[] second = new string[9] { "Top Left", "Top Middle", "Top Right", "Middle Right", "Bottom Right", "Bottom Center", "Bottom Left", "Left Center", "Center" };
			Require(AvatarStandardPoseGrid.CaptureOrderKeys.Select(AvatarStandardPoseGrid.GetDisplayName).SequenceEqual(second), "The human Standard Model capture order changed.");
			Require(AvatarStandardPoseGrid.Classify(15.0, -15.0, 179.0) == "A0/B0", "The practical Center tolerance stopped accepting an inclusive 15-degree A/B pose with arbitrary C roll.");
			Require(AvatarStandardPoseGrid.Classify(15.01, 0.0, 0.0) == "A+/B0", "The Bottom Center direction did not begin beyond the Center tolerance.");
			Require(AvatarStandardPoseGrid.Classify(0.0, -15.01, 0.0) == "A0/B-", "The Left Center direction did not begin beyond the Center tolerance.");
			for (int num = 0; num < array.Length; num++)
			{
				(double, double, string) tuple = array[num];
				double value = (double)num + 1.0;
				AvatarStandardPoseSample avatarStandardPoseSample = AvatarStandardPoseGrid.CreateSample("sample-" + tuple.Item3, DateTime.UtcNow, tuple.Item1, tuple.Item2, 0.0, 95.0, 0.001, 3840, 2160, mediaPipeLandmarks, CreateShape(value), CreateCanonicalIdentity(value));
				Require(avatarStandardPoseSample.DirectionKey == tuple.Item3, $"Classified {tuple.Item1}/{tuple.Item2} as {avatarStandardPoseSample.DirectionKey}, expected {tuple.Item3}.");
				Require(avatarStandardPoseSample.MediaPipeLandmarks.Count == 478, "Pose sample did not retain all 478 MediaPipe landmarks.");
				Require(AvatarStandardPoseGrid.HasCompleteIdentityEvidence(avatarStandardPoseSample), "Pose sample did not retain complete FLAME identity evidence.");
				dictionary[avatarStandardPoseSample.DirectionKey] = avatarStandardPoseSample;
			}
			AvatarStandardIdentityFusionResult avatarStandardIdentityFusionResult = AvatarStandardIdentityFusion.Fuse(dictionary);
			Require(avatarStandardIdentityFusionResult.PoseEvidenceCount == 9, "Identity fusion did not use all nine pose slots.");
			Require(!avatarStandardIdentityFusionResult.UsesLegacyAnchor, "Complete identity evidence incorrectly used a legacy anchor.");
			Require(avatarStandardIdentityFusionResult.ShapeCoefficients.All((double num2) => Math.Abs(num2 - 5.0) < 1E-09), "Nine-pose coefficient fusion did not preserve equal evidence from every direction.");
			Require(avatarStandardIdentityFusionResult.CanonicalIdentityVertices.All((FaceMeshLandmarkPoint point) => Math.Abs(point.X - 5.0) < 1E-09), "Nine-pose canonical fusion did not preserve equal evidence from every direction.");
			Require(AvatarStandardPoseGrid.IsComplete(dictionary.Keys), "The complete nine-direction atlas was not recognized as complete.");
			Require(AvatarStandardPoseGrid.IsComplete(dictionary), "The complete nine-direction measurement atlas was not recognized as complete.");
			dictionary.Remove("A0/B0");
			Require(!AvatarStandardPoseGrid.IsComplete(dictionary.Keys), "An eight-direction atlas was incorrectly recognized as complete.");
			AvatarStandardPoseSample avatarStandardPoseSample2 = AvatarStandardPoseGrid.CreateSample("replacement-center", DateTime.UtcNow, 0.0, 0.0, 1.0, 97.0, 0.0001, 3840, 2160, mediaPipeLandmarks, CreateShape(50.0), CreateCanonicalIdentity(50.0));
			dictionary[avatarStandardPoseSample2.DirectionKey] = avatarStandardPoseSample2;
			Require(dictionary.Count == 9, "Replacing one direction changed the fixed nine-slot atlas size.");
			Require(dictionary["A0/B0"].ObservationId == "replacement-center", "The center pose slot was not replaced.");
			Require(AvatarStandardPoseGrid.IsComplete(dictionary), "The replaced nine-direction measurement atlas was not recognized as complete.");
			AvatarStandardIdentityFusionResult avatarStandardIdentityFusionResult2 = AvatarStandardIdentityFusion.Fuse(dictionary);
			Require(avatarStandardIdentityFusionResult2.ShapeCoefficients.All((double num2) => Math.Abs(num2 - 10.0) < 1E-09), "Replacing one pose did not recompute the equal-evidence identity fusion.");
			Require(avatarStandardIdentityFusionResult2.CanonicalIdentityVertices.All((FaceMeshLandmarkPoint point) => Math.Abs(point.X - 10.0) < 1E-09), "Replacing one pose did not recompute canonical identity geometry.");
			AvatarStandardModelStore avatarStandardModelStore = new AvatarStandardModelStore();
			avatarStandardModelStore.Write(outputFolder, new AvatarStandardModel
			{
				SubjectId = "pose-grid-self-test",
				SubjectDisplayName = "Pose Grid Self Test",
				CompletedImageCount = dictionary.Count,
				IdentityEvidencePoseCount = avatarStandardIdentityFusionResult2.PoseEvidenceCount,
				ShapeCoefficients = avatarStandardIdentityFusionResult2.ShapeCoefficients,
				CanonicalIdentityVertices = avatarStandardIdentityFusionResult2.CanonicalIdentityVertices,
				PoseAtlas = dictionary
			});
			AvatarStandardModel? avatarStandardModel = avatarStandardModelStore.Read(outputFolder);
			Require((object)avatarStandardModel != null, "The Standard Model JSON did not round-trip.");
			Require(avatarStandardModel.PoseAtlas.Count == 9, "The Standard Model JSON lost pose-atlas slots.");
			Require(avatarStandardModel.PoseAtlas["A0/B0"].MediaPipeLandmarks.Count == 478, "The Standard Model JSON lost exact MediaPipe landmarks.");
			Require(avatarStandardModel.PoseAtlas["A0/B0"].IdentityShapeCoefficients.Count == 100, "The Standard Model JSON lost per-pose identity coefficients.");
			Require(avatarStandardModel.PoseAtlas["A0/B0"].CanonicalIdentityVertices.Count == 1000, "The Standard Model JSON lost per-pose canonical identity geometry.");
			Require(avatarStandardModel.ShapeCoefficients.SequenceEqual(avatarStandardIdentityFusionResult2.ShapeCoefficients), "The Standard Model JSON changed fused DECA seed coefficients.");
			return new AvatarStandardPoseGridSelfTestResult(Succeeded: true, "Standard Model pose-grid self-test passed: ordered 9 directions, practical Center tolerance, fixed-slot replacement, per-pose FLAME identity retention, equal-evidence fusion, and fused seed round-trip.");
		}
		catch (Exception ex)
		{
			return new AvatarStandardPoseGridSelfTestResult(Succeeded: false, "Standard Model pose-grid self-test failed: " + ex.Message);
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}

	private static IReadOnlyList<double> CreateShape(double value)
	{
		return Enumerable.Repeat(value, 100).ToArray();
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreateCanonicalIdentity(double value)
	{
		return (from index in Enumerable.Range(0, 1000)
			select new FaceMeshLandmarkPoint
			{
				Index = index,
				X = value,
				Y = value * 2.0,
				Z = value * 3.0
			}).ToArray();
	}
}
