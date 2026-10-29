using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Bounce As…: every deliverable shape, at every depth we promise.</summary>
public class BounceTests
{
    [Fact]
    public void Writes24BitPcm()
    {
        using var materializedStream = new MemoryStream();
        var shortBuffer = new StereoBuffer(10, 48000);
        shortBuffer.Left[0] = 1f;
        WaveFileMaterializer.Write(materializedStream, shortBuffer, bitsPerSample: 24);
        var materializedBytes = materializedStream.ToArray();

        Assert.Equal(44 + 10 * 6, materializedBytes.Length);
        Assert.Equal(24, BitConverter.ToInt16(materializedBytes, 34));
        Assert.Equal(48000, BitConverter.ToInt32(materializedBytes, 24));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x7F }, materializedBytes[44..47]);
    }

    [Fact]
    public void SceneBounceLoopsOneSceneAndStemsCoverAudibleTracks()
    {
        var starterProject = ProjectSerializer.CreateStarterProject("bounce");
        var sceneBuffer = OfflineArrangementRenderer.RenderScene(starterProject, sceneIndex: 1, loopCount: 2, sampleRate: 8000);
        var expectedSeconds = 2 * starterProject.SceneLengthInSteps(1) * starterProject.StepDurationSeconds + 1.5;
        Assert.InRange(sceneBuffer.DurationSeconds, expectedSeconds - 0.01, expectedSeconds + 0.01);

        var stems = OfflineArrangementRenderer.RenderStems(starterProject, stemProject => OfflineArrangementRenderer.Render(stemProject, 8000)).ToList();
        Assert.Equal(starterProject.Tracks.Count, stems.Count);
        Assert.All(stems, stem => Assert.True(stem.Stem.PeakAmplitude() > 0.001f, $"{stem.Track.Name} stem is silent"));
    }
}
