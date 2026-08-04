# Auto Eat Low Health

血量低于阈值时自动弹出进食询问,选择期间无敌。可设置优先吃的食物与无敌帧开关,联机可用。

## 功能

- 低血触发:受到伤害后血量低于阈值(默认 40%)→ 弹窗询问
- 必死保命:必死一击(裸伤害 ≥ 当前血)直接拦截,进入保护
- 连续自动吃:选"吃!"/对话中触发/弹窗 3 秒无回应 → 连续吃到血回阈值上或食物吃完
- 弹窗询问:吃!(按优先级) / 我要吃别的... / 不吃
- 自选菜单:打开期间全程无敌,关掉才结束
- 三层兜底:受击前缀(必死) + 受击后缀(低血) + 每帧心跳
- 联机同步:保护用游戏内 buff(NetField 同步),进食无敌用原版 isEating(NetEvent 同步),房主访客一致
- 冷却:两次触发最小间隔(默认 5 秒,防围殴轰炸)

## 配置

`config.json`(或用 Generic Mod Config Menu):

- `HealthThreshold`:触发血量阈值(0.2~0.5)
- `InvincibleWhilePrompt`:选择期间无敌(必死拦截始终生效)
- `CooldownSeconds`:触发冷却(秒)
- `FoodPriority`:进食优先级(QualifiedItemId 列表,越靠前越先吃)

SMAPI 控制台命令:`eat_priority` 打开优先级设置界面。

## 构建

```bash
dotnet build -c Release
```

需要 SMAPI 4.x + Stardew Valley 1.6.15。
