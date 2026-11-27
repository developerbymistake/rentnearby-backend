using System.Security.Cryptography;
using System.Text.Json;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Tests.Stays.Quotation;

// Q19 / ARCHITECTURE 9.4: bank and UPI at rest. AES-GCM with the purpose as associated data: a blob opens only for the same owner
// and purpose, a DL blob never opens as bank data (and the other way), old taxi licence blobs still open, nothing plain when off.
public class QuoteBankCipherTests
{
    private const string Plain = """{"holder":"Banyan Retreat","accountNo":"123456789012","ifsc":"SBIN0001234","bank":"SBI","branch":"Ramnagar","upi":"banyan@sbi"}""";

    [Fact]
    public void Blob_is_not_the_plain_text_and_opens_with_the_same_purpose()
    {
        var cipher = QuoteKit.Cipher();
        var user = Guid.NewGuid();
        var blob = cipher.Protect(Plain, StayQuoteProfileService.BankPurpose(user));
        Assert.DoesNotContain("123456789012", blob);
        Assert.StartsWith("v1.", blob);
        Assert.Equal(Plain, cipher.Unprotect(blob, StayQuoteProfileService.BankPurpose(user)));
    }

    [Fact]
    public void A_blob_moved_to_another_owner_does_not_open()
    {
        var cipher = QuoteKit.Cipher();
        var blob = cipher.Protect(Plain, StayQuoteProfileService.BankPurpose(Guid.NewGuid()));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Unprotect(blob, StayQuoteProfileService.BankPurpose(Guid.NewGuid())));
    }

    [Fact]
    public void Bank_and_licence_blobs_never_open_as_each_other()
    {
        var cipher = QuoteKit.Cipher();
        var user = Guid.NewGuid();
        var dl = cipher.Protect("UK0420190012345");
        Assert.ThrowsAny<CryptographicException>(() => cipher.Unprotect(dl, StayQuoteProfileService.BankPurpose(user)));
        var bank = cipher.Protect(Plain, StayQuoteProfileService.BankPurpose(user));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Unprotect(bank));
    }

    [Fact]
    public void Old_licence_blobs_still_open_through_the_old_overload_and_the_taxi_purpose()
    {
        var cipher = QuoteKit.Cipher();
        var dl = cipher.Protect("UK0420190012345");
        Assert.Equal("UK0420190012345", cipher.Unprotect(dl));
        Assert.Equal("UK0420190012345", cipher.Unprotect(dl, AesGcmIdentityCipher.TaxiDlPurpose));
    }

    [Fact]
    public void Unconfigured_cipher_refuses_instead_of_storing_plain()
    {
        var off = QuoteKit.Cipher(configured: false);
        Assert.False(off.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => off.Protect(Plain, "stay-quote-bank:x"));
    }

    [Fact]
    public void Empty_purpose_is_refused() => Assert.Throws<ArgumentException>(() => QuoteKit.Cipher().Protect(Plain, ""));

    [Fact]
    public void Hub_summary_masks_and_never_carries_the_full_number_or_upi()
    {
        var user = Guid.NewGuid();
        var service = QuoteKit.Profile(null!);
        var blob = QuoteKit.Cipher().Protect(Plain, StayQuoteProfileService.BankPurpose(user));
        var (summary, unreadable) = service.Summary(user, blob);
        Assert.False(unreadable);
        Assert.True(summary.Has);
        Assert.Equal(("SBI", "Ramnagar", "9012", "SBIN0001234", "ba***@sbi"), (summary.Bank, summary.Branch, summary.AccountLast4, summary.Ifsc, summary.UpiMasked));
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("123456789012", json);
        Assert.DoesNotContain("banyan@sbi", json);
    }

    [Fact]
    public void Unreadable_blob_is_reported_not_thrown()
    {
        var service = QuoteKit.Profile(null!);
        var other = QuoteKit.Cipher().Protect(Plain, StayQuoteProfileService.BankPurpose(Guid.NewGuid()));
        var (summary, unreadable) = service.Summary(Guid.NewGuid(), other);
        Assert.True(unreadable);
        Assert.False(summary.Has);
        Assert.Equal((QuoteBankSummaryDto.None, false), service.Summary(Guid.NewGuid(), null));
    }

    [Fact]
    public void Bank_block_is_normalised_before_encryption()
    {
        var bank = StayQuoteProfileService.Normalise(new QuoteBankRequest
        {
            Holder = "  Banyan  ", AccountNo = " 123456789012 ", Ifsc = "sbin0001234", Bank = "", Branch = null, Upi = " banyan@sbi ",
        });
        Assert.Equal(new QuoteBankDto("Banyan", "123456789012", "SBIN0001234", null, null, "banyan@sbi"), bank);
    }
}
