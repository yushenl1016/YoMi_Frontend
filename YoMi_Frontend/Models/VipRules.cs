using System;
using System.Collections.Generic;
using System.Linq;

namespace YoMi_Frontend.Models
{
    public sealed class VipTier
    {
        public int Level { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public decimal Threshold { get; init; }
        public decimal Discount { get; init; }
        public string CssClass { get; init; } = string.Empty;
        public string DiscountLabel { get; init; } = string.Empty;
        public string Tag { get; init; }
        public IReadOnlyList<string> Benefits { get; init; } = Array.Empty<string>();
    }

    public sealed class VipStatus
    {
        public decimal Total { get; init; }
        public VipTier Tier { get; init; }
        public decimal PreviousThreshold { get; init; }
        public decimal? NextThreshold { get; init; }
        public decimal Progress { get; init; }
    }

    public static class VipRules
    {
        private static readonly VipTier General = new()
        {
            Level = 0,
            Name = "一般會員",
            Icon = "⭐",
            Threshold = 0,
            Discount = 1m,
            CssClass = "vip-general",
            DiscountLabel = "一般價格"
        };

        public static IReadOnlyList<VipTier> Tiers { get; } = new[]
        {
            new VipTier
            {
                Level = 1, Name = "銀悠咪", Icon = "🥈", Threshold = 3000m, Discount = .97m,
                CssClass = "vip-silver", DiscountLabel = "基礎單享 97 折",
                Benefits = new[] { "專屬 yumi 標籤身分組", "基礎單享 97 折優惠", "繼承所有一般會員福利" }
            },
            new VipTier
            {
                Level = 2, Name = "金悠咪", Icon = "🥇", Threshold = 6000m, Discount = .97m,
                CssClass = "vip-gold", DiscountLabel = "全場消費享 97 折", Tag = "推薦",
                Benefits = new[] { "全場消費享 97 折優惠", "每 2 月抽獎報名名額", "抽獎獎品包含刀皮、季度趣味單及年度大型獎品", "繼承所有銀悠咪福利" }
            },
            new VipTier
            {
                Level = 3, Name = "白金悠咪", Icon = "🔷", Threshold = 15000m, Discount = .97m,
                CssClass = "vip-platinum", DiscountLabel = "新晉白金首單享 9 折",
                Benefits = new[] { "建立老闆與所有打手的專屬群組", "月／年底中獎機率提升", "新晉白金首單享 9 折優惠", "繼承所有金悠咪福利" }
            },
            new VipTier
            {
                Level = 4, Name = "鑽悠咪", Icon = "💎", Threshold = 30000m, Discount = .95m,
                CssClass = "vip-diamond", DiscountLabel = "全場 95 折優惠",
                Benefits = new[] { "全場消費享 95 折優惠", "繼承所有白金悠咪福利" }
            },
            new VipTier
            {
                Level = 5, Name = "至尊悠咪", Icon = "👑", Threshold = 99999m, Discount = .9m,
                CssClass = "vip-supreme", DiscountLabel = "全場 9 折優惠", Tag = "最高等級",
                Benefits = new[] { "全場消費享 9 折優惠", "優先排單權（非指定打手情況）", "每月一次免費絕密保底 1000W 單", "專屬語音頻道", "繼承所有鑽悠咪福利" }
            }
        };

        public static VipStatus GetStatus(decimal total)
        {
            total = Math.Max(0, total);
            var tier = Tiers.LastOrDefault(x => total >= x.Threshold) ?? General;
            var next = Tiers.FirstOrDefault(x => x.Threshold > total);
            var progress = next == null
                ? 100m
                : Math.Min(100m, (total - tier.Threshold) / (next.Threshold - tier.Threshold) * 100m);

            return new VipStatus
            {
                Total = total,
                Tier = tier,
                PreviousThreshold = tier.Threshold,
                NextThreshold = next?.Threshold,
                Progress = progress
            };
        }

        public static void SelfCheck()
        {
            if (GetStatus(2999m).Tier.Level != 0 ||
                GetStatus(3000m).Tier.Level != 1 ||
                GetStatus(99999m).Tier.Level != 5 ||
                GetStatus(-1m).Progress != 0m)
            {
                throw new InvalidOperationException("VIP rules are inconsistent.");
            }
        }
    }
}
