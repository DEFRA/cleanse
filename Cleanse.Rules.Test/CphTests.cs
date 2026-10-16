using Cleanse.Rules.Domain;

namespace Cleanse.Rules.Test;

public class CphTests
{
    [Theory]
    [InlineData("01/036/8000")]
    [InlineData("99/999/9999")]
    [InlineData("00/000/0000")]
    public void Parses_a_well_formed_cph(string value)
    {
        Assert.True(Cph.TryParse(value, out var cph));
        Assert.Equal(value, cph.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2045")]            // a CTS slaughterhouse
    [InlineData("'2045'")]          // a CTS slaughterhouse as the export quotes it
    [InlineData("1/036/8000")]
    [InlineData("01/36/8000")]
    [InlineData("01/036/800")]
    [InlineData("01-036-8000")]
    [InlineData("01/036/8000/01")]  // a CPHH, not a CPH
    [InlineData("AH-01/036/8000")]
    [InlineData("ab/cde/fghi")]
    public void Rejects_anything_that_is_not_a_cph(string? value)
    {
        Assert.False(Cph.TryParse(value, out _));
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        Assert.True(Cph.TryParse("  01/036/8000 ", out var cph));
        Assert.Equal("01/036/8000", cph.Value);
    }

    [Fact]
    public void Exposes_the_county_code()
    {
        Assert.Equal(8, Cph.Parse("08/205/8000").CountyCode);
    }

    [Fact]
    public void An_uninitialised_cph_has_no_value()
    {
        var cph = default(Cph);
        Assert.False(cph.HasValue);
        Assert.Throws<InvalidOperationException>(() => cph.Value);
    }
}
