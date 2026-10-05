using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QRCoder;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Orders;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Authorize, Route("orders"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class OrdersController : Controller
    {
        private readonly CustomerOrderService _orders;
        private readonly PortalService _portal;
        private readonly ILogger<OrdersController> _logger;
        private int MemberId => AuthCookie.GetMemberId(User);
        public OrdersController(CustomerOrderService orders, PortalService portal, ILogger<OrdersController> logger)
        { _orders = orders; _portal = portal; _logger = logger; }

        [HttpGet("")]
        public async Task<IActionResult> Index(int page = 1, bool history = false)
        {
            page = Math.Clamp(page, 1, 100000);
            var orders = await _orders.ListAsync(MemberId, page, history);
            return View(new MyOrdersViewModel { Page = page, History = history, HasNext = orders.Count > 20, Orders = orders.Take(20).ToList() });
        }

        [HttpGet("new")]
        public async Task<IActionResult> New(int itemId = 0, bool custom = false) =>
            View(await FormAsync(new OrderInput { ItemId = custom ? 0 : itemId, IsCustom = custom, RequestId = Guid.NewGuid() }));

        [HttpPost("review"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Review(OrderInput input)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var model = await FormAsync(input);
                    model.Preview = await _orders.PreviewAsync(MemberId, input, OrderRules.TaiwanNow);
                    if (!model.Preview.PayableAmount.HasValue) return View("New", model);
                    input.ExpectedAmount = model.Preview.PayableAmount;
                    input.PriceVersion = model.Item.UpdatedAt.Ticks;
                    ModelState.Remove(nameof(input.ExpectedAmount)); ModelState.Remove(nameof(input.PriceVersion));
                    return View(model);
                }
                catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); }
            }
            return View("New", await FormAsync(input));
        }

        [HttpPost("create"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(OrderInput input)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var order = await _orders.CreateAsync(MemberId, input, OrderRules.TaiwanNow);
                    TempData["Success"] = order.Status == CustomerOrderStatus.AwaitingQuote ? "需求已送出，請聯絡客服確認報價。" : "訂單已建立，請依付款資訊完成付款。";
                    return RedirectToAction(nameof(Detail), new { id = order.Id });
                }
                catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); }
                catch (Exception ex)
                { _logger.LogError(ex, "Order creation failed for member {MemberId}", MemberId); ModelState.AddModelError("", "暫時無法確認送出結果，請先到「我的訂單」查看，再使用原表單重試。"); }
            }
            return View("New", await FormAsync(input));
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Detail(Guid id)
        {
            var order = await _orders.FindAsync(MemberId, id);
            if (order == null) return NotFound();
            return View(new OrderDetailViewModel { Order = order, Settings = await _portal.GetSettingsAsync(), HasProof = await _orders.HasProofAsync(MemberId, id) });
        }

        [HttpGet("{id:guid}/transfer-qr")]
        public async Task<IActionResult> TransferQr(Guid id)
        {
            var order = await _orders.FindAsync(MemberId, id);
            if (order == null || order.Status != CustomerOrderStatus.AwaitingPayment || !(order.PayableAmount > 0)) return NotFound();
            var settings = await _portal.GetSettingsAsync();
            var account = PaymentAccount.Parse(settings.TryGetValue("payment_transfer", out var value) ? value : null);
            if (account == null) return NotFound();
            using var data = QRCodeGenerator.GenerateQrCode(account.TransferPayload(order.PayableAmount.Value), QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data);
            return File(png.GetGraphic(8), "image/png");
        }

        [HttpPost("{id:guid}/proof"), ValidateAntiForgeryToken]
        [RequestSizeLimit(OrderRules.MaxProofBytes + 1048576), RequestFormLimits(MultipartBodyLengthLimit = OrderRules.MaxProofBytes + 1048576)]
        public Task<IActionResult> UploadProof(Guid id, int version, string method, IFormFile proof) => ExecuteAsync(id, async () =>
        {
            if (proof == null || proof.Length <= 0 || proof.Length > OrderRules.MaxProofBytes) throw new ArgumentException("請選擇 5 MB 以下的 JPG、PNG 或 WebP 付款證明。");
            using var stream = new MemoryStream();
            await proof.CopyToAsync(stream);
            await _orders.UploadProofAsync(MemberId, id, version, stream.ToArray(), method, OrderRules.TaiwanNow);
        }, "付款證明已送出，等待客服核對入帳。");

        [HttpGet("{id:guid}/proof")]
        public async Task<IActionResult> Proof(Guid id)
        {
            var proof = await _orders.ProofAsync(MemberId, id);
            return proof == null ? NotFound() : File(proof.Data, proof.ContentType);
        }

        [HttpPost("{id:guid}/cancel"), ValidateAntiForgeryToken]
        public Task<IActionResult> Cancel(Guid id) => ExecuteAsync(id, () => _orders.CancelAsync(MemberId, id, OrderRules.TaiwanNow), "未付款訂單已取消。");

        private async Task<OrderFormViewModel> FormAsync(OrderInput input)
        {
            var items = await _orders.ItemsAsync();
            return new OrderFormViewModel { Input = input, Items = items, Item = input.IsCustom ? null : items.FirstOrDefault(x => x.Id == input.ItemId) };
        }
        private async Task<IActionResult> ExecuteAsync(Guid id, Func<Task> action, string success)
        {
            if (await _orders.FindAsync(MemberId, id) == null) return NotFound();
            try
            {
                if (!ModelState.IsValid) throw new ArgumentException("資料格式不正確，請重新確認。");
                await action(); TempData["Success"] = success;
            }
            catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
            catch (Exception ex)
            { _logger.LogError(ex, "Order update failed for {OrderId}", id); TempData["Error"] = "操作未完成，請重新整理查看訂單狀態後再試。"; }
            return RedirectToAction(nameof(Detail), new { id });
        }
    }
}
