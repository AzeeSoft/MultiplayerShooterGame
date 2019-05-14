using UnityEngine;
using UnityEngine.Events;

public class Shootable : MonoBehaviour
{
    public class ShootableEvent: UnityEvent<float, Vector3> {}
    
    public ShootableEvent onShotEvent;

    public void OnShot(float damage, Vector3 shotPosition)
    {
        onShotEvent.Invoke(damage, shotPosition);
    }
}