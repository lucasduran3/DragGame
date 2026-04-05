using UnityEngine;

/// <summary>
/// Suma cuántos brazos están agarrados y lo comunica al AlienPhysicsBody
/// para que ajuste el drag dinámicamente.
/// Colocar en el mismo GameObject raíz.
/// </summary>
public class GrippedArmCounter : MonoBehaviour
{
    public AlienPhysicsBody physicsBody;
    public ArmProbe[] arms;

    void Update()
    {
        if (physicsBody == null) return;
        int count = 0;
        foreach (var arm in arms)
            if (arm != null && arm.IsGripped) count++;
        physicsBody.GrippedArmCount = count;
    }
}