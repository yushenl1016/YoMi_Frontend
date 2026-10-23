# 桌寵下載頁與 ZIP 發行

## 2026-10-23：0.7.4 單一 EXE 安裝 ZIP

固定輸出 `D:/Sixdots/Project/YoMi/YoMi_Desktop_Pet_Release`，ZIP、SHA、release.json 同層。網站下載 ZIP 僅含 `YoMi_Desktop_Pet_Setup.exe`；manifest 的 optional `update` 是完整 runtime ZIP，Latest API 仍用原 `zip` 回應格式，使 0.7.3 可直接 App 內升級。必須先部署新版前台，最後啟用 manifest。安裝器保留 Data，舊 portable 使用者選原 EXE 所在資料夾；不同站台／降版／執行中會拒絕。Google API 模式不再讀舊匯入設定，帳號畫面移除匯入入口；服務失敗提示重試。54項桌寵、11項打包、5項安裝器、25項下載、20項更新API通過，實際新裝及0.7.3升級使用localhost假資料通過。正式config API200；未執行真實Google授權或部署正式站。舊MSIX_Test未變更。詳細證據 `verification/installer-0.7.4/verification-report.json`。

下載功能整合在 YoMi_Frontend，沿用既有 ASP.NET Core MVC、共用版型、桌面與手機導覽，不需要另一個網站專案。

- 頁面：`/desktop-pet`
- ZIP：`/desktop-pet/download`（匿名、GET/HEAD、Range 續傳）
- 正式頁面：`https://yumiclub.duckdns.org:8082/desktop-pet`
- 2026-10-23：0.7.3 桌寵與下載包已設定正式站；部署由管理者執行。下方較早驗證紀錄保留供參考。
- 本次未部署正式站，未修改資料庫、會員或現有桌寵 Data。

## 本機啟動

在 YoMi_Frontend 專案根目錄執行：

```powershell
dotnet build .\YoMi_Frontend\YoMi_Frontend.csproj
.\tools\Start-DesktopPetBackend.ps1
```

若 5101 已使用，沿用現有服務，不要重複啟動。後端設定見 [DesktopPetDatabase.md](DesktopPetDatabase.md)。

## 產生新版 ZIP

固定交付位置：`D:\Sixdots\Project\YoMi\YoMi_Desktop_Pet_Release`。ZIP、`.sha256`、`release.json` 放在同一層；打包工具省略 `--output` 時也使用此位置。上傳至網站 `App_Data/DesktopPetReleases` 時先傳 ZIP 與 SHA，最後更新 manifest。此位置只改變發行交付目錄，不改變網站讀取路徑。

先更新桌寵 `resources/app/package.json` 版本；以 Python 3.11 以上執行。0.7.4 起另需官方 NSIS 編譯器：用 `--makensis <makensis.exe完整路徑>`，或先設定 `YOMI_MAKENSIS` 環境變數。範例命令假設已設定該環境變數：

```powershell
python .\tools\build_desktop_release.py --source "D:\Sixdots\Project\YoMi\YoMi_Desktop_Pet" --output "D:\Sixdots\Project\YoMi\YoMi_Desktop_Pet_Release" --backend "https://yumiclub.duckdns.org:8082"
```

打包前請暫停修改桌寵程式碼；無需搬動或清除現有 Data。工具驗證必要執行檔、入口、相依套件、路徑與 ZIP CRC，產生版本 ZIP、SHA-256 文字檔及 `release.json`。同名版本拒絕覆蓋；請升版。頁面直接顯示 manifest 對應的版本與實際大小，不需改寫 Razor。

發行內容只包括 Electron、桌寵程式、資產、原生模組、Google 驗證函式庫、音樂助手與授權檔案；排除 Data、TestData、seed、備份、Google 用戶端設定、登入憑證及私密目錄。包含 Google 函式庫不等於包含登入設定。首次執行自行建立訪客資料，預設 keyboard、120 幣。

目前 ZIP 連正式站；Google 設定由 /api/desktop/auth/config 自動取得，下載者無須匯入 JSON。正式主機需要部署 Google 設定 API，並在站台根目錄上一層的 local-secrets 放置 desktop-google.json；desktop-api.json 的 GoogleClientIds 必須包含該電腦版 client ID。私密設定及任何人的登入狀態均不放入下載 ZIP。

## 發布與更新

`YoMi_Frontend/App_Data/DesktopPetReleases/` 位於 wwwroot 外，已排除 Git 與 dotnet publish。部署前台時要**另外複製該發行目錄**。下載控制器只接受符合版本檔名、大小、SHA 格式及 HTTPS 後端的 manifest，未準備好時顯示暫不可下載。

正式站需一起部署桌寵 API 與伺服器端 Google 設定，再啟用已設定正式網址的 ZIP；部署後驗證 /api/desktop/auth/config 與 /api/desktop/releases/latest。此次未執行正式部署。

0.7.3 起使用 App 內更新，下載、儲存、安裝與重啟由程式完成。未具備更新入口的舊版本需先移轉至新版；任何移轉都必須保留原 Data。詳見 ../../YoMi_Desktop_Pet/docs/IN_APP_UPDATE.md。

## 驗證

```powershell
python -m unittest discover -s tools -p test_build_desktop_release.py
dotnet run --project .\tools\DesktopDownloadChecks\DesktopDownloadChecks.csproj
```

本次驗證：打包測試 6 項、MVC/manifest 檢查 14 項；實際 HTTP 下載 SHA-256 相符，HEAD 200、Range 206、越界 Range 416、私有發行路徑 404、桌寵 API 未登入 401。解壓後以獨立測試資料啟動並正常退出，確認新訪客及 keyboard/120 幣預設。原桌寵仍在執行，因此測試副本的本機房間有預期的 33111 連接埠占用訊息；不屬於缺檔錯誤。

瀏覽器已確認桌面畫面、正確版本、FAQ 展開及校驗碼複製。窄版 CSS 與手機導覽已加入；尚未做實體手機／另一台 Windows 的驗證。編譯沿用既有 System.Data.SqlClient 4.8.5 NU1903 警告，這次沒有升級共用資料庫套件。

## 結構與素材

- `Controllers/DesktopDownloadController.cs`：頁面與下載路由。
- `Services/DesktopPetReleaseService.cs`：固定發行目錄與 manifest 驗證。
- `ViewModels/DesktopPetReleaseViewModel.cs`：頁面資料。
- `Views/DesktopDownload/Index.cshtml`：共用版型、功能／安裝／更新／FAQ。
- `wwwroot/css/desktop-pet.css`、`wwwroot/js/desktop-pet.js`：頁面樣式與複製校驗碼。
- `wwwroot/images/desktop-pet/hero.webp`：使用者指定網站公開角色素材。

參考網站：https://yumiclubapi-ke7u9etu.manus.space/ 。對照使用者提供的 yumi-club-server.zip 內 Download.tsx，將原 EXE 安裝與更新文案調整為此桌寵實際功能與 ZIP 流程。角色素材來源路徑：`/manus-storage/yumi-download-hero_2995aed0.webp`。

## 0.7.1

下載包已包含登入自動續期，仍使用 https://localhost:5101。已完成實際下載與 SHA-256 比對；現有桌寵重啟後成功續期，保留原會員與進度。原 0.7.0 ZIP 保留，manifest 已指向 0.7.1；舊版不含自動續期，請下載新版並保留 Data。

## 2026-10-18：免費發行信任度與完整性

維持全部桌寵功能、現有版本與 API 設定；不重新壓縮、修改或簽署現有 ZIP/MSIX，因此檔案 SHA-256 保持不變。網站下載仍為 0.7.1 / localhost；私人商店草稿為 0.7.2.0 r2 / 正式 API，兩者不可混用。

- Download 現在用同一個已開啟的串流計算 SHA-256 並提供下載；與 manifest 不同即拒絕，包括同樣大小的損壞／替換檔案。HTTP Range、HEAD、ETag、附件檔名與既有 nosniff 保留。每次下載會讀完整 ZIP 做校驗；大量流量前須量測 I/O。
- 下載頁補齊尚未簽章、SmartScreen 提示與 SHA-256 核對方式；校驗相符不等於安全保證或微軟認證。不要求關閉防毒、加排除、移除網際網路來源標記、匯入自簽根憑證或用管理員執行桌寵。
- 現有 ZIP 與 MSIX 以 Microsoft Defender 自訂掃描均未發現威脅。掃描啟用 DisableRemediation 只避免本次檢查隔離／刪除檔案，沒有關閉即時防護；檢查結果詳見 `../YoMi_Desktop_Pet/verification/release-trust-report.json`。這不代表 SmartScreen 信譽已建立。
- 16 項下載檢查及 6 項打包測試通過；實際本機下載 SHA-256 一致，HEAD 200、Range 206，頁面說明可呈現。沒有資料庫寫入或登入測試。
- 正式站 `/desktop-pet/download` 本次 GET 為 404，HEAD 為 405；尚未部署這次網站修改，不能宣稱外部使用者已取得改善。

後續每次發行：使用固定 HTTPS 官方下載來源；保留已發行的原始 ZIP 與校驗碼，不為改文案重打同版本。程式內容變更才升版；沿用既有拒絕覆蓋的打包工具。部署前比對來源 ZIP、部署後下載 ZIP 與 manifest 三者 SHA-256。先掃描再公開；若有具體威脅偵測，先停止該版發布並處理誤判或問題，不以排除項目繞過。不要為了「洗信譽」反覆提交相同檔案。

免費且保持功能的前提下，這些措施保護發行檔完整性與減少使用者疑慮，無法替代可信程式碼簽章或保證不顯示 SmartScreen。未加入免費自簽或測試憑證，也未購買任何服務。未變更第三方素材、音樂功能、Google 設定或會員進度。

官方依據：https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation

## 2026-10-18：正式下載部署檔已備妥（由使用者部署）

正式站頁面 HTTP 200、API 匿名 HTTP 401，但下載為 404 且頁面顯示檔案準備中。已建立正式 API 的 0.7.2 ZIP，SHA-256：`2351e4dfc73ffb9f1afcbf3a3bc82ef55d67aa3c58e1467864b9f562707a717c`。舊 0.7.1 與開發版設定不變。

部署資料夾：`D:\Sixdots\Project\YoMi\.codex-artifacts\YoMi_Desktop_Pet\production-download-20261018\ReadyToDeploy`，內附部署步驟與檔案校驗報告。01 更新前台程式、02 新增 ZIP、03 最後啟用 release.json；不覆蓋站台設定或使用者資料。新包 Defender 掃描未發現威脅，7 項打包與 16 項下載檢查通過。

使用者選擇自行部署，尚未對正式主機寫入；必須部署後實際下載並核對 SHA-256，才能宣告正式下載 404 已修復。
