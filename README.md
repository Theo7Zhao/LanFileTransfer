# TheoTransfer · Theo文件传输

局域网手机 ↔ Windows 文件互传工具。电脑运行本软件，手机扫码即可互传文件，**手机免装 App、免输地址**（扫码即达）。传输走局域网直连，不消耗流量、不经任何第三方服务器。

* 版本：1.6.1
* 平台：Windows 10/11 x64
* 技术栈：.NET 10 · WPF（桌面端）+ ASP.NET Core Kestrel（HTTP 服务）+ 原生 HTML/JS（手机端）

* * *

## 功能特性

| 特性  | 说明  |
| --- | --- |
| 手机 → 电脑 | 网页选文件 / 拍照 / 录像 / 拖入，自动上传到接收文件夹 |
| 电脑 → 手机 | 文件拖入窗口（或点「添加文件…」），手机网页下载 / 预览 |
| 主客双模式 | 访客模式一键连接、主机模式扫码自动连接（见下文） |
| 分块上传 | 4MB 分块直写磁盘，大文件不受内存限制，实时显示速度与进度 |
| 断点续传 | 上传分块自动重试（6 次退避）+ offset 对齐续传；下载支持 HTTP Range |
| 端口容错 | 配置端口被占时自动改用 49152–65535 空闲端口，弹非阻断通知 |
| 玻璃拟态 UI | Windows 11 亚克力（Acrylic）质感，Win10 自动回退不透明底色 |
| 中文友好 | 中文文件名正常处理，重名自动追加 `(1)`、`(2)` |

## 主客双模式（v1.6+）

电脑窗口左上角切换，凭据直接拼进二维码 URL：

* **访客模式（默认）**——适合临时给别人传文件。6 位配对码拼入二维码（`?code=xxxxxx`），对方扫码后**点一下「连接」**即完成验证；配对码可随时刷新，刷新即失效。
* **主机模式**——适合自己的常用设备。8 位静态密钥（已剔除易混字符）持久化保存，拼入二维码（`?key=xxxxxxxx`），手机扫码**零点击**自动连接；密钥在 PC 端可手动刷新，刷新后旧密钥与已建立的连接全部失效。

两种模式共用防护：按 IP 连续输错 5 次锁定 30 秒（锁定期间正确凭据也拒绝）；会话令牌 12 小时有效、活跃自动续期；凭据比较用固定时间算法防时序侧信道。

## 技术架构

    ┌─────────────┐  HTTP/1.1 (局域网直连)   ┌──────────────────────┐
    │  手机浏览器   │ ◄─────────────────────► │  Windows (WPF 窗口)   │
    │  WebUI.html │   扫码 / 配对 / 传输      │  Kestrel HTTP 服务    │
    └─────────────┘                         │  + 传输记录 / 共享管理 │
                                            └──────────────────────┘

* **传输协议**：HTTP/1.1（明文，手机浏览器原生支持；选 FTP 的对比分析见《协议说明.txt》）
* **手机 → 电脑**：`POST /api/upload/init` 建会话 → `PUT /api/upload/chunk` 按 4MB 分块直写磁盘 → `POST /api/upload/complete` 落盘
* **电脑 → 手机**：`GET /api/outbox/{id}` 下载，支持 `Range` 断点续传与 `inline` 预览
* **鉴权**：`/api/pair` 换取令牌 → 后续请求携带 `X-Auth-Token`；`/api/*`（除配对/信息）统一中间件校验
* **手机端**：单文件 HTML（内嵌进 exe 作为资源），无任何外部依赖

## 项目结构

    TheoTransfer\
    ├─ TheoTransfer.csproj         项目文件（名称、版本、图标配置）
    ├─ App.xaml / App.xaml.cs      应用入口
    ├─ MainWindow.xaml(.cs)        主界面（模式切换、二维码、传输记录、端口策略）
    ├─ README.md                   本文件
    ├─ 打包说明.md                 如何打包成 exe（一条命令）
    ├─ 协议说明.txt                传输协议与鉴权设计细节
    ├─ Assets\                     图标（logo.ico / app.ico，编译时内嵌）
    └─ Core\
       ├─ WebUI.html               手机端网页（内嵌进 exe，含公共场所安全提醒）
       ├─ TransferServer.cs        HTTP 服务（路由、上传/下载、鉴权中间件）
       ├─ AppCore.cs               核心逻辑（配对码、静态密钥、会话、上传会话）
       ├─ AppSettings.cs           设置持久化（%APPDATA%\TheoTransfer\settings.json）
       ├─ TransferRecord.cs        传输记录模型（进度/速度/状态）
       ├─ UploadSession.cs         上传会话（分块写入、断点对齐）
       └─ SharedFile.cs            共享文件模型

## 快速开始

**环境要求**：.NET SDK 10（`dotnet --list-sdks` 能看到 10.x）；IDE 可选（VS 2022+ / Rider / VS Code 均可）。

    # 还原依赖
    dotnet restore TheoTransfer.csproj
    
    # 直接运行（开发调试）
    dotnet run
    
    # 打包成单文件 exe（约 70MB，目标电脑无需安装任何运行时）
    dotnet publish TheoTransfer.csproj -c Release -r win-x64 --self-contained true ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
      -p:IncludeNativeLibrariesForSelfExtract=true -o ..\publish

发布产物只有一个 `TheoTransfer.exe`，发给同事即可直接使用（详细参数说明见《打包说明.md》）。

**使用流程**：双击 exe → Windows 防火墙弹窗点「允许」→ 手机连同一 Wi-Fi 扫码 → 访客模式点「连接」/ 主机模式自动进入 → 互传文件。接收文件夹默认在用户目录 `TheoTransfer`，可在窗口中自定义并持久化记忆。

## 安全说明

* 传输内容**未加密**（明文局域网直连）：局域网点对点 + 凭据鉴权场景下加密收益低，手机端页面顶部有常驻公共场所安全提醒（机场/咖啡厅/酒店等公共 Wi-Fi 慎传敏感文件）
* 所有凭据（配对码 / 静态密钥）仅存于本机 `%APPDATA%\TheoTransfer\settings.json` 与内存
* 若未来需跨公网使用，升级 HTTPS 即可

## 更多文档

* 《打包说明.md》——环境、打包命令、参数说明、分发指引、常见问题
* 《协议说明.txt》——协议选型、双模式鉴权设计、安全边界
* 《publish/使用说明.txt》——面向最终用户的操作指引
