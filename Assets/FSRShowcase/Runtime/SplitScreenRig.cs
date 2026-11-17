using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace FSRShowcase
{
    /// <summary>
    /// Two-player split-screen: the main camera takes the left half, a second base camera the right half. URP keeps one upscaler
    /// context per camera, so each view gets its own FSR context and temporal history.
    /// </summary>
    public sealed class SplitScreenRig
    {
        Camera m_Main, m_Second;
        Transform m_Pose;
        Vector3 m_PosB;
        Quaternion m_RotB;
        float m_Time;

        public bool Active => m_Second != null;
        public Camera SecondCamera => m_Second;

        public void Enable(Camera main, Transform poseB)
        {
            if (Active || main == null)
                return;

            m_Main = main;
            m_Pose = poseB;
            var clone = Object.Instantiate(main.gameObject);
            clone.name = "SplitScreen Camera B";
            clone.tag = "Untagged";
            foreach (Transform child in clone.transform)
                Object.Destroy(child.gameObject);
            foreach (var c in clone.GetComponents<Component>())
            {
                if (c is Transform || c is Camera || c is UniversalAdditionalCameraData)
                    continue;
                Object.Destroy(c);
            }
            clone.transform.SetParent(null, true);

            m_Second = clone.GetComponent<Camera>();
            m_Second.depth = main.depth + 1f;
            m_Second.rect = new Rect(0.5f, 0f, 0.5f, 1f);
            m_Main.rect = new Rect(0f, 0f, 0.5f, 1f);

            m_PosB = poseB != null ? poseB.position : main.transform.position;
            m_RotB = poseB != null ? poseB.rotation : main.transform.rotation;
            m_Second.transform.SetPositionAndRotation(m_PosB, m_RotB);
            m_Time = 0f;
        }

        public void Disable()
        {
            if (m_Main != null)
                m_Main.rect = new Rect(0f, 0f, 1f, 1f);
            if (m_Second != null)
                Object.Destroy(m_Second.gameObject);
            m_Second = null;
        }

        /// <summary>Slow sideways sway of the second view so the temporal upscaler has motion to resolve.</summary>
        public void Update(float dt)
        {
            if (m_Second == null)
                return;
            m_Time += dt;
            var right = m_RotB * Vector3.right;
            m_Second.transform.position = m_PosB + right * (Mathf.Sin(m_Time * 0.6f) * 1.2f);
        }
    }
}
