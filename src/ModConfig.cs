namespace AutoEatLowHealth;

public class ModConfig
{
    /// <summary>触发血量阈值(0.2 = 20%)。</summary>
    public float HealthThreshold { get; set; } = 0.2f;

    /// <summary>触发/进食期间无敌。</summary>
    public bool InvincibleWhilePrompt { get; set; } = true;

    /// <summary>两次触发的最短间隔(秒)。</summary>
    public int CooldownSeconds { get; set; } = 8;

    /// <summary>进食优先级(QualifiedItemId,越靠前越先吃)。</summary>
    public List<string> FoodPriority { get; set; } = new();
}
