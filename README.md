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
