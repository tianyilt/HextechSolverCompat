# Publication status / 发布状态（2026-10-05）

**October8 status / 10月8日最新状态：** [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814083983) is now publicly accessible without login; actual title/description were read back with HTTP200 and no hidden-item error. The earlier automatic-content-check hold has cleared for the public page. [PR224](https://github.com/Torch1230/CombatSolver/pull/224) was merged on October6. This does not change the compatibility package's pinned0.48.1/macOS scope, nor turn the recorded historical performance gate into Passed. Below is the preserved earlier publication history.

**工坊已公开：**10月8日匿名回读实际物品页面与说明，HTTP200，无隐藏错误；此前检查不再阻止公开访问。PR224已于10月6合并。兼容包仍固定0.48.1/macOS，不因PR合并宣称新版兼容或历史性能Passed。以下保留原发布过程。

[Source / 源码](https://github.com/tianyilt/HextechSolverCompat) · [macOS preview / macOS预览发行包](https://github.com/tianyilt/HextechSolverCompat/releases/tag/v0.1.0-preview-macos) · [Steam Workshop / 工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3814083983) · [PR224](https://github.com/Torch1230/CombatSolver/pull/224)

The source and GitHub prerelease are publicly accessible. Steam accepted the Workshop upload and reports Public metadata with no ban; the exact bilingual description, three dependencies, cover, two gameplay images and all five downloaded package files have been verified. **On October6, the logged-in author page explicitly reports that the item is waiting for Steam's automatic content check and will remain temporarily hidden until the check clears.** The cover and both gameplay images display on the actual owner page. Public Workshop availability is still pending. The earlier email/permission explanations were unconfirmed possibilities; the owner banner now establishes the actual pending stage. No email-confirmation request is shown in the observed banner. No re-upload or account change was made.

源码与GitHub预览发行包已公开。Steam已接受工坊上传，原生属性为Public且未被封禁；中英文全文、三项依赖、封面、两张实战图及五个下载文件均回读一致。**10月6日解锁后，已登录作者页面明确提示正在等待Steam自动内容检查，通过前暂时隐藏。** 实际页面正常显示封面和两张实战图；公开可见性仍待平台检查完成。此前邮箱/权限等说法是未确认的可能性，现在已定位实际等待环节。所观察横幅未要求确认邮件，没有重新上传或改账号设置。

This is a **pinned CombatSolver0.48.1 macOS preview**: game0.111.0 / HextechRunes0.9.7 / RitsuLib0.6.5. The current98-mechanism matrix has234 strictly verified inputs:232 native passed (including manual-continuation guards) and two exact expected refusals. The68 targeted checks and four guards are separate overlapping suites, not extra inputs to add. Windows, a fresh complete run and newer dependency builds remain unverified. [Testing methods, evidence and failures](TESTING.md) explain the scope.

这是**固定求解器0.48.1的macOS预览版**，依赖游戏0.111.0、海克斯0.9.7、RitsuLib0.6.5。同构建98机制/234输入完成严格核验（232正常，含手动续玩守卫；2确切拒绝）。68专项和4守卫另记，不累计重叠输入。Windows、当前全新整局及新版依赖仍未验证。

PR224 was initially submitted as Draft on October5; at the user's request, it was marked Ready for review on October6 with the pending acceptance items disclosed. This requests maintainer review and does not assert merge readiness or complete validation. Its generic hand-limit state changes were rebased onto0.50.0 and built without warnings/errors. Published native evidence and timing belong to0.48.1; performance acceptance is NotPassed, and fresh0.50.0 native/performance checks remain pending. Both remote PR images and the exact submitted body were read back.

PR224最初以Draft提交；2026-10-06按用户要求转为Ready for review并公开保留验收缺口，不表示可直接合并或全部验收通过：通用手牌上限状态改动已在0.50.0基础上重定基并构建通过；公开原生测试和计时属于0.48.1历史证据，性能验收为NotPassed，0.50.0新原生/性能验证待补。实际PR正文与两张远端图片已回读。

[Machine-readable publication receipt / 发布核验收据](publication-verification.json) records the public/native-tested DLL distinction: only128bytes in the private external-PDB filename differ; all other PE bytes and3,326 method contracts match. This is not a claim that the altered debug metadata DLL itself ran all234 native tests.
