using KineticTrainer.Workouts.Playback;
using Xunit;

namespace KineticTrainer.Tests.Workouts;

public class CueSpeechTests
{
    [Theory]
    [InlineData("Effort: 200 W for 1 minute", "Effort: 200 watts for 1 minute")]
    [InlineData("Warm-up: 125–175 W for 10 minutes", "Warm-up: 125 to 175 watts for 10 minutes")]
    [InlineData("Recover: 1 W for 30 seconds", "Recover: 1 watt for 30 seconds")]
    [InlineData("Next: Interval 1 of 13, 295 W for 30 seconds in 30 seconds", "Next: Interval 1 of 13, 295 watts for 30 seconds in 30 seconds")]
    [InlineData("Settle into a steady endurance gear at 68% FTP", "Settle into a steady endurance gear at 68 percent of F T P")]
    [InlineData("Warm-up: 50–70% FTP for 5 minutes", "Warm-up: 50 to 70 percent of F T P for 5 minutes")]
    [InlineData("Fast pedalling 1 of 3: 1 min at 100 rpm", "Fast pedalling 1 of 3: 1 min at 100 R P M")]
    [InlineData("Interval 2: MAX for 10 seconds", "Interval 2: max for 10 seconds")]
    [InlineData("Workout complete. Estimated FTP 249 W. Stop the ride to save it.", "Workout complete. Estimated F T P 249 watts. Stop the ride to save it.")]
    [InlineData("3", "3")]
    [InlineData("  ", "")]
    public void Units_are_spoken_as_words(string screen, string spoken)
    {
        Assert.Equal(spoken, CueSpeech.ToSpoken(screen));
    }

    [Fact]
    public void Words_that_merely_contain_w_are_untouched()
    {
        Assert.Equal("Wow, well done 2 watts", CueSpeech.ToSpoken("Wow, well done 2 W"));
        Assert.Equal("Sweet Spot 2x20 W1", CueSpeech.ToSpoken("Sweet Spot 2x20 W1"));
    }
}
