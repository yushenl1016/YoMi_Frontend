using System;
using System.Collections.Generic;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Admin.Library.Models.Orders;

namespace YoMi_Frontend.ViewModels
{
    public class OrderFormViewModel
    {
        public OrderInput Input { get; set; } = new();
        public FrontendPriceItem Item { get; set; }
        public IReadOnlyList<FrontendPriceItem> Items { get; set; } = Array.Empty<FrontendPriceItem>();
        public CustomerOrder Preview { get; set; }
    }
    public class MyOrdersViewModel
    {
        public IReadOnlyList<CustomerOrder> Orders { get; set; }
        public bool History { get; set; }
        public int Page { get; set; }
        public bool HasNext { get; set; }
    }
    public class OrderDetailViewModel
    {
        public CustomerOrder Order { get; set; }
        public IReadOnlyDictionary<string, string> Settings { get; set; }
        public bool HasProof { get; set; }
        public string Get(string key) => Settings.TryGetValue(key, out var value) ? value : "";
        public string ContactUrl(string key) => Uri.TryCreate(Get(key), UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http") ? uri.AbsoluteUri : null;
    }
}
