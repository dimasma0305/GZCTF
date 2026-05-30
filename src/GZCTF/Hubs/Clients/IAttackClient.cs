using GZCTF.Models.Request.Game;

namespace GZCTF.Hubs.Clients;

/// <summary>
/// Client interface for the public AttackHub.
/// </summary>
public interface IAttackClient
{
    /// <summary>
    /// Receive an attack event (any flag submission for the game).
    /// </summary>
    public Task ReceivedAttack(AttackEvent evt);

    /// <summary>
    /// Receive a King-of-the-Hill control-change event (a hill's holder changed).
    /// Drives the hill objective nodes on the attack animation page.
    /// </summary>
    public Task ReceivedKothControl(KothControlEvent evt);
}
