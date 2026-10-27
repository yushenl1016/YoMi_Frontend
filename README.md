# YoMi_Frontend

依照 `youmi_esports.zip` 畫面與功能重建的 ASP.NET Core MVC 前台，並直接參考
`YoMi_Admin.Library`，與 `YoMi_Admin` 共用 SQL Server 資料庫。

## 功能

- 首頁、價目表、VIP 制度與動態網站文案
- Email 註冊／登入、會員資料、消費紀錄與 VIP 進度
- 管理後台：會員、消費、價目、網站設定、背景素材及管理員權限
- 圖片／影片上傳，以及 YouTube、Bilibili 背景網址

## 啟動

```powershell
dotnet run --project .\YoMi_Frontend\YoMi_Frontend.csproj
```

資料表 migration 位於 `YoMi_Admin/YoMi_Admin/Migrations`，已加入並套用
`AddFrontendPortal`。新環境可在 `YoMi_Admin` 專案執行：

```powershell
dotnet ef database update --project .\YoMi_Admin.Library\YoMi_Admin.Library.csproj --startup-project .\YoMi_Admin\YoMi_Admin.csproj
```

## 首位管理員

在 `appsettings.json`、環境變數或部署設定中，把 `BootstrapAdminEmail` 設成預定
管理員 Email；該 Email 首次註冊時會取得 `admin` 權限。建立後可清空設定，其他
管理員由 `/admin` 的「管理員」頁籤指派。

正式環境請以 Secret／環境變數覆寫 `ConnectionStrings__DefaultConnection`，不要把
正式密碼提交到版本控制。

## Discord 訂單通知

下單交易成功後立即喚醒前台背景程序處理通知，不等待 Discord 回應才完成下單。背景程序仍每 15 秒掃描共用資料庫，補送待送通知並處理後台確認收款的通知，無須同時運行後台發送服務。私密設定放在 `YoMi_Frontend/.secrets/discord-orders.json`，設定 `DiscordOrders:Enabled` 為 `true` 啟用；檔案已排除 Git 與發布輸出，部署時需另行放置。Webhook 不會傳給瀏覽器。

前後台須連到同一個已套用通知資料表遷移的資料庫。本機修改後請停止 VS 偵錯、重新建置並啟動前台，再下單測試；正常下單會立即觸發發送，實際送達時間仍受佇列負載、網路與 Discord 限流影響。完整設定、資料庫更新順序與重試限制見 [後台 README 的 Discord 訂單通知](../YoMi_Admin/README.md#discord-訂單通知)。

## YoMi_Desktop_Pet 資料庫

桌寵沿用本專案的 SQL Server 與 FrontendMember，既有 YoMiDocuments／YoMiSessions 改以 FrontendMemberId 關聯；YoMiUsers 已移除。Google 登入、HTTPS API 與每 15 秒自動同步已接通。設定、資料保留遷移及本機後端啟動方式見 [桌寵資料庫與 API 說明](tools/DesktopPetDatabase.md)。

## 桌寵下載

前台導覽的「桌寵下載」連至 `/desktop-pet`，提供乾淨 ZIP、安裝／保留 Data 更新說明及檔案校驗碼。沿用既有 MVC 版型，不需要另建網站。打包、localhost 測試與部署發行目錄方式見 [桌寵下載維護說明](tools/DesktopPetDownload.md)。
