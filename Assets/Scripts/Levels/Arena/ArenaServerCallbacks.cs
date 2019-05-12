using System.Collections.Generic;
using Bolt;
using JetBrains.Annotations;
using UnityEngine;

[BoltGlobalBehaviour(BoltNetworkModes.Server, "ArenaTestScene")]
public class ArenaServerCallbacks : Bolt.GlobalEventListener
{
    private Randomizer<Transform> _spawnPointsRandomizer;

    public override void SceneLoadLocalDone(string scene)
    {
        base.SceneLoadLocalDone(scene);

        _spawnPointsRandomizer = new Randomizer<Transform>(ArenaLevelManager.Instance.SpawnPoints);
        OnSceneReady();
    }

    public override void SceneLoadRemoteDone(BoltConnection connection)
    {
        base.SceneLoadRemoteDone(connection);
        
        OnSceneReady(connection);
    }

    void OnSceneReady(BoltConnection connection = null)
    {
        Transform spawnPoint = _spawnPointsRandomizer.GetRandomItem();
        if (spawnPoint != null)
        {
            NetworkPlayerRegistry.SpawnPlayer(spawnPoint.position, spawnPoint.rotation, connection);
        }
    }
}