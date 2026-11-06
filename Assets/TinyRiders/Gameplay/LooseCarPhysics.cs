using UnityEngine;

namespace TinyRiders
{
    /// <summary>Shared collision geometry and solver settings for live cars and pile baking.</summary>
    public static class LooseCarPhysics
    {
        public static Bounds WorldBounds(Bounds body, Vector3 position, Quaternion rotation)
        {
            int direction = body.size.z >= body.size.x ? 2 : 0;
            float width = direction == 2 ? body.size.x : body.size.z;
            float radius = Mathf.Max(.01f, Mathf.Min(width, body.size.y) * .5f);
            float length = direction == 2 ? body.size.z : body.size.x;
            Vector3 axis = rotation * (direction == 2 ? Vector3.forward : Vector3.right);
            Vector3 extent = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) *
                Mathf.Max(0, length * .5f - radius) + Vector3.one * radius;
            return new Bounds(position + rotation * body.center, extent * 2f);
        }

        public static void Fit(CapsuleCollider capsule, Bounds body)
        {
            capsule.direction = body.size.z >= body.size.x ? 2 : 0;
            float width = capsule.direction == 2 ? body.size.x : body.size.z;
            capsule.radius = Mathf.Max(.01f, Mathf.Min(width, body.size.y) * .5f);
            capsule.height = Mathf.Max(capsule.radius * 2f, capsule.direction == 2 ? body.size.z : body.size.x);
            capsule.center = body.center;
            capsule.contactOffset = .002f;
        }

        public static void Configure(Rigidbody body)
        {
            body.mass = 1f;
            body.linearDamping = 3f;
            body.angularDamping = 4f;
            body.solverIterations = 12;
            body.solverVelocityIterations = 4;
            body.sleepThreshold = .01f;
            body.maxDepenetrationVelocity = 1.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
    }
}
