using WinMux.Core.Update;
using WinMux.Shell.Update;

namespace WinMux.Shell.Tests;

/// <summary>
/// When an available update is put in front of the user.
///
/// Until 2026-09-24 it never was: an available update wrote one line in the status bar, six seconds
/// after startup, saying to press <c>Ctrl+B</c> then <c>:</c> and pick "Install update". The check
/// worked and always had; the offer was the part that did not exist.
/// </summary>
public class UpdateOfferTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text);

    [Fact]
    public void An_update_is_offered_the_first_time_it_is_seen() =>
        Assert.True(new UpdateOffer().ShouldOffer(V("0.7.7")));

    [Fact]
    public void And_not_on_every_check_after_that()
    {
        // The check runs on a timer. Without this the same dialog arrives until it is accepted,
        // which teaches people to close it without reading.
        var offer = new UpdateOffer();

        Assert.True(offer.ShouldOffer(V("0.7.7")));
        Assert.False(offer.ShouldOffer(V("0.7.7")));
        Assert.False(offer.ShouldOffer(V("0.7.7")));
    }

    [Fact]
    public void A_newer_version_is_a_new_question()
    {
        // Declining 0.7.7 is not a decision about 0.8.0.
        var offer = new UpdateOffer();
        offer.ShouldOffer(V("0.7.7"));

        Assert.True(offer.ShouldOffer(V("0.8.0")));
    }

    [Fact]
    public void Asking_for_a_check_by_name_offers_again()
    {
        var offer = new UpdateOffer();
        offer.ShouldOffer(V("0.7.7"));

        offer.Reset();

        Assert.True(offer.ShouldOffer(V("0.7.7")));
    }
}
