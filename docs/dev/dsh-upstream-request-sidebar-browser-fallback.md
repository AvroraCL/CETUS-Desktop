# Feature request 草稿：侧边浏览器对拒绝嵌入网站提供回退

> 提交对象：DeepSeek Harness 上游仓库（提交前请按仓库模板补全环境信息；
> 以下基于 `@deepseek-ai/dsh@0.1.6-alpha.2` 的逆向与实测，可附复现细节）。

## 标题

Sidebar web browser: offer a fallback for sites that refuse iframe embedding (X-Frame-Options / CSP frame-ancestors)

## 现象

侧边"浏览器"面板以 `<iframe src="外部网址">` 方式渲染网页。相当数量的主流网站
发送 `X-Frame-Options: deny / SAMEORIGIN` 或 CSP `frame-ancestors`，浏览器内核
（WebView2 / Chrome / Edge，行为一致）会拒绝渲染并显示"拒绝访问 / 拒绝连接"，
面板内没有任何恢复手段。

实测（2026-09，0.1.6-alpha.2）：

- `https://github.com` → `X-Frame-Options: deny` + `CSP frame-ancestors 'none'` → 拒绝
- `https://www.bing.com` → `X-Frame-Options: SAMEORIGIN` → 拒绝
- `https://example.com` → 未发送限制头 → 正常显示

该行为与承载页面无关（官方 Web 版在任意浏览器中一致），属 iframe 方案的固有限制。

## 影响

- 用户在侧边浏览器中访问受保护/主流站点时面板不可用，且没有引导性提示
- 桌面壳（如 CETUS）无法在不重做产品的情况下修复——绕过需要代理剥除安全头，
  这会削弱网站自身的安全语义

## 建议（任选其一或组合）

1. **回退按钮（成本最低）**：检测 iframe 加载失败/被拒（`x-frame-options` 拒绝时
   iframe 会触发 load 事件但内容为错误页，可在渲染层配合站点特征提示），在面板
   内提供"在系统浏览器打开此页面"按钮。
2. **后端抓取渲染模式（可选开关）**：由 `web/fetch` 后端代理页面文本/可读化内容
   （类似阅读模式）。需注意会话凭据不外泄、脚本不执行，避免安全回归。
3. **文档明示**：在面板空态文案中说明"部分网站禁止被嵌入，请使用系统浏览器打开"，
   降低用户困惑。

## 环境信息

- `@deepseek-ai/dsh@0.1.6-alpha.2`（Windows 11 24H2，宿主为 CETUS 桌面壳与
  Chrome/Edge 直开均可复现）
- 复现步骤：`dsh web` → 打开侧边浏览器面板 → 输入 `https://github.com` → 面板
  显示"拒绝访问"
