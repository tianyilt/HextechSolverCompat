# Publication status / 发布状态（2026-10-05）

[Source / 源码](https://github.com/tianyilt/HextechSolverCompat) · [macOS preview / macOS预览发行包](https://github.com/tianyilt/HextechSolverCompat/releases/tag/v0.1.0-preview-macos) · [Steam Workshop / 工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3814083983) · [PR224](https://github.com/Torch1230/CombatSolver/pull/224)

The source and GitHub prerelease are publicly accessible. Steam accepted the Workshop upload and reports Public metadata with no ban; the exact bilingual description, three dependencies, cover, two gameplay images and all five downloaded package files have been verified. **The unauthenticated Workshop page still reports hidden/no permission, so public Workshop availability has not been verified.** The owner's Steam page must be inspected to establish the reason. [Steam Support](https://help.steampowered.com/en/wizard/HelpWithUGCSubmission/) describes email confirmation and moderation as possible gates; these are possibilities, not a diagnosis. The Workshop EULA status query returned InvalidParam and cannot establish agreement status.

源码与GitHub预览发行包已公开。Steam已接受工坊上传，原生属性为Public且未被封禁；中英文全文、三项依赖、封面、两张实战图及五个下载文件均回读一致。**匿名工坊页面仍显示隐藏/无权限，尚不能确认其他玩家可访问。** 需在作者登录状态下查看具体提示；邮箱确认或审核是Steam官方列出的可能环节，当前未确定是哪一种。协议状态查询返回InvalidParam，也不能据此断言已确认协议。

This is a **pinned CombatSolver0.48.1 macOS preview**: game0.111.0 / HextechRunes0.9.7 / RitsuLib0.6.5. The current98-mechanism matrix has234 strictly verified inputs:232 native passed (including manual-continuation guards) and two exact expected refusals. The68 targeted checks and four guards are separate overlapping suites, not extra inputs to add. Windows, a fresh complete run and newer dependency builds remain unverified. [Testing methods, evidence and failures](TESTING.md) explain the scope.

这是**固定求解器0.48.1的macOS预览版**，依赖游戏0.111.0、海克斯0.9.7、RitsuLib0.6.5。同构建98机制/234输入完成严格核验（232正常，含手动续玩守卫；2确切拒绝）。68专项和4守卫另记，不累计重叠输入。Windows、当前全新整局及新版依赖仍未验证。

PR224 was initially submitted as Draft on October5; at the user's request, it was marked Ready for review on October6 with the pending acceptance items disclosed. This requests maintainer review and does not assert merge readiness or complete validation. Its generic hand-limit state changes were rebased onto0.50.0 and built without warnings/errors. Published native evidence and timing belong to0.48.1; performance acceptance is NotPassed, and fresh0.50.0 native/performance checks remain pending. Both remote PR images and the exact submitted body were read back.

PR224最初以Draft提交；2026-10-06按用户要求转为Ready for review并公开保留验收缺口，不表示可直接合并或全部验收通过：通用手牌上限状态改动已在0.50.0基础上重定基并构建通过；公开原生测试和计时属于0.48.1历史证据，性能验收为NotPassed，0.50.0新原生/性能验证待补。实际PR正文与两张远端图片已回读。

[Machine-readable publication receipt / 发布核验收据](publication-verification.json) records the public/native-tested DLL distinction: only128bytes in the private external-PDB filename differ; all other PE bytes and3,326 method contracts match. This is not a claim that the altered debug metadata DLL itself ran all234 native tests.
