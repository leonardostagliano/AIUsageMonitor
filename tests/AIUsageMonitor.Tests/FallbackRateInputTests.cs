using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.Tests;

public class FallbackRateInputTests
{
    [Theory]
    [InlineData("1,14", 1.14)]
    [InlineData("1.14", 1.14)] // the WPF it-IT converter read this as 114, then clamped to 2,00
    [InlineData("1.1411", 1.1411)]
    [InlineData(" 1,1411 ", 1.1411)]
    [InlineData("1", 1.0)]
    [InlineData("0,5", 0.5)]
    [InlineData("2", 2.0)]
    public void Either_decimal_separator_gives_the_typed_rate(string text, double expected)
    {
        Assert.Equal(expected, FallbackRateInput.Parse(text, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.140,5")] // a thousands separator
    [InlineData("1,1.4")]
    [InlineData("-1,14")]
    [InlineData("1e0")]
    public void Anything_but_a_plain_decimal_number_is_refused(string text)
    {
        Assert.Null(FallbackRateInput.Parse(text, out var error));
        Assert.Equal("Scrivi il tasso come numero, per esempio 1,14.", error);
    }

    [Theory]
    [InlineData("0,49")]
    [InlineData("2.01")]
    [InlineData("114")]
    public void A_rate_out_of_range_is_refused_instead_of_clamped(string text)
    {
        Assert.Null(FallbackRateInput.Parse(text, out var error));
        Assert.Equal("Il tasso deve essere tra 0,5 e 2,0.", error);
    }

    [Fact]
    public void The_rate_is_shown_the_Italian_way_and_reads_back_the_same()
    {
        Assert.Equal("1,14", FallbackRateInput.Format(1.14));
        Assert.Equal("1,1411", FallbackRateInput.Format(1.1411));
        Assert.Equal(1.1411, FallbackRateInput.Parse(FallbackRateInput.Format(1.1411), out _));
    }
}
