using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Models;

namespace YoMi_Frontend.ViewModels
{
    public sealed class SiteChromeViewModel
    {
        public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
        public FrontendBackgroundAsset Background { get; init; }

        public string Get(string key, string fallback = "") =>
            Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    public sealed class HomePageViewModel
    {
        public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

        public string Get(string key, string fallback = "") =>
            Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    public sealed class PricePageViewModel
    {
        public string SelectedTab { get; init; } = "基礎";
        public IReadOnlyList<FrontendPriceItem> Items { get; init; } = Array.Empty<FrontendPriceItem>();
    }

    public sealed class VipPageViewModel
    {
        public IReadOnlyList<VipTier> Tiers { get; init; } = Array.Empty<VipTier>();
        public VipStatus Status { get; init; }
    }

    public sealed class RecordsPageViewModel
    {
        public FrontendMember Member { get; init; }
        public IReadOnlyList<FrontendConsumptionRecord> Records { get; init; } = Array.Empty<FrontendConsumptionRecord>();
        public VipStatus Status { get; init; }
    }

    public sealed class AccountViewModel
    {
        [Required(ErrorMessage = "請輸入郵箱")]
        [EmailAddress(ErrorMessage = "請輸入有效的郵箱")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入密碼")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [StringLength(64)]
        public string ReturnUrl { get; set; }
    }

    public sealed class RegisterViewModel
    {
        [Required(ErrorMessage = "請輸入用戶名稱")]
        [StringLength(64, ErrorMessage = "用戶名稱不可超過 64 個字")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入郵箱")]
        [EmailAddress(ErrorMessage = "請輸入有效的郵箱")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入密碼")]
        [StringLength(128, MinimumLength = 8, ErrorMessage = "密碼至少需要 8 個字符")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "請再次輸入密碼")]
        [Compare(nameof(Password), ErrorMessage = "兩次輸入的密碼不相同")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public sealed class AdminMemberViewModel
    {
        public FrontendMember Member { get; init; }
        public decimal Total { get; init; }
        public VipStatus Status { get; init; }
    }

    public sealed class AdminStatsViewModel
    {
        public int TotalUsers { get; init; }
        public int VipMembers { get; init; }
        public decimal TotalSpending { get; init; }
        public decimal AverageSpending { get; init; }
    }

    public sealed class AdminPageViewModel
    {
        public string ActiveTab { get; init; } = "users";
        public string PriceTab { get; init; } = "基礎";
        public string Search { get; init; } = string.Empty;
        public AdminStatsViewModel Stats { get; init; } = new();
        public IReadOnlyList<AdminMemberViewModel> Members { get; init; } = Array.Empty<AdminMemberViewModel>();
        public IReadOnlyList<FrontendConsumptionRecord> SelectedRecords { get; init; } = Array.Empty<FrontendConsumptionRecord>();
        public FrontendMember SelectedMember { get; init; }
        public IReadOnlyList<FrontendPriceItem> PriceItems { get; init; } = Array.Empty<FrontendPriceItem>();
        public IReadOnlyList<FrontendSiteSetting> Settings { get; init; } = Array.Empty<FrontendSiteSetting>();
        public IReadOnlyList<FrontendBackgroundAsset> Backgrounds { get; init; } = Array.Empty<FrontendBackgroundAsset>();
    }
}
