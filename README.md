# OmniToolbox.yuzi.Test

Yuzi 的 OmniToolbox 在线模块测试仓库。此仓库用于在正式发布前，通过 Omni 在线模块功能安装和验证候选模块。

## 在 Omni 中添加测试库链

在“插件设置 → 在线模块”中添加以下清单地址：

```text
https://raw.githubusercontent.com/yuanzil1243-oss/OmniToolbox.yuzi.Test/main/TreeHouseModules.json
```

测试清单只登记当前需要用户验证的模块。模块默认关闭，添加后请在“树树妙妙屋 → 在线”中手动启用。在线模块具有与 Omni 相同的运行权限，只安装可信来源的模块。

测试清单的模块显示名称带有“（测试）”后缀，模块文件沿用正式仓库版本。当前测试条目包括：隐藏目标圈（测试）、成就追踪（测试）、快捷出售（测试）和极限技调整（测试）。

## 测试与正式发布

- 测试模块源码放在 `Modules/`，并在 `TreeHouseModules.json` 中单独登记。
- 每个发布文件只包含一个在线模块；清单中的版本、`ApiVersion`、`MinimumOmniVersion`、`File` 和 `Sha256` 必须与模块及文件一致。
- 测试完成并由用户明确确认后，才将已验证模块晋级到正式仓库 [OmniToolbox.yuzi](https://github.com/yuanzil1243-oss/OmniToolbox.yuzi)。
- 不要将测试条目加入正式清单，也不要把未经用户验证的候选模块标记为正式发布。

## 已废弃模块

ImeCandidateScaling（Dalamud 插件候选栏缩放）已停止后续开发和更新，不再登记在活动测试清单中。历史源码和说明保存在 Modules/Deprecated/，仅用于追溯，不应安装。
