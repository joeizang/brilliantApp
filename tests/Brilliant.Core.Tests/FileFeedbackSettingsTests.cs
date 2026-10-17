using Brilliant.Core.Feedback;

namespace Brilliant.Core.Tests;

/// <summary>The learner's choice to get feedback through the server (#167): off until they turn it on, and kept across restarts.</summary>
public sealed class FileFeedbackSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "feedback-settings-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "feedback.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void It_is_off_by_default()
    {
        Assert.False(new FileFeedbackSettings(Path_).UseServer);
    }

    [Fact]
    public void Turning_it_on_is_remembered_by_the_next_start()
    {
        new FileFeedbackSettings(Path_).UseServer = true;

        Assert.True(new FileFeedbackSettings(Path_).UseServer);
    }

    [Fact]
    public void Turning_it_off_again_is_remembered_too()
    {
        var settings = new FileFeedbackSettings(Path_);
        settings.UseServer = true;
        settings.UseServer = false;

        Assert.False(new FileFeedbackSettings(Path_).UseServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"useServer":"yes"}""")]
    [InlineData("null")]
    public void A_damaged_file_means_off(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, content);

        Assert.False(new FileFeedbackSettings(Path_).UseServer);
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_throw_and_the_choice_holds_for_this_run()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path_);   // a directory where the file should be: every write fails

        var settings = new FileFeedbackSettings(Path_);
        settings.UseServer = true;

        Assert.True(settings.UseServer);
    }
}
