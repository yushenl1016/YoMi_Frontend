using Microsoft.AspNetCore.Mvc;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using YoMi_Frontend.Models;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    public class HomeController : Controller
    {
        private static readonly string[] PriceTabs = { "基礎", "趣味", "訂製" };
        private readonly PortalService _portal;

        public HomeController(PortalService portal)
        {
            _portal = portal;
        }

        public async Task<IActionResult> Index()
        {
            return View(new HomePageViewModel { Settings = await _portal.GetSettingsAsync() });
        }

        [HttpGet("price")]
        public async Task<IActionResult> Price(string tab = "基礎")
        {
            if (Array.IndexOf(PriceTabs, tab) < 0) tab = "基礎";
            return View(new PricePageViewModel
            {
                SelectedTab = tab,
                Items = await _portal.GetPriceItemsAsync(tab)
            });
        }

        [HttpGet("vip")]
        public async Task<IActionResult> Vip()
        {
            VipStatus status = null;
            var memberId = AuthCookie.GetMemberId(User);
            if (memberId > 0)
            {
                status = VipRules.GetStatus(await _portal.GetTotalConsumptionAsync(memberId));
            }

            return View(new VipPageViewModel { Tiers = VipRules.Tiers, Status = status });
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }

        [HttpGet("status/{code:int}")]
        public IActionResult StatusCodePage(int code)
        {
            Response.StatusCode = code;
            return View("StatusCode", code);
        }
    }
}
