using TMPro;
using UnityEngine;

namespace HilbertLoop
{
    /// <summary>NPC 低声交谈时头顶的短句气泡（几秒后自动消失）。</summary>
    public class NPCConversationBubble : MonoBehaviour
    {
        public float lifetime = 7f;

        TextMeshPro _text;
        Camera _camera;
        float _dieAt;

        public static NPCConversationBubble Spawn(Transform parent, string text)
        {
            GameObject go = new GameObject("ChatBubble");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 1.12f, 0f);
            go.transform.localScale = Vector3.one * 0.045f;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = 36f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 0.93f, 0.72f, 0.95f);
            ChineseFontUtility.Apply(tmp);

            var bubble = go.AddComponent<NPCConversationBubble>();
            bubble._text = tmp;
            bubble._camera = Camera.main;
            bubble._dieAt = Time.time + bubble.lifetime;
            return bubble;
        }

        void Update()
        {
            if (Time.time >= _dieAt)
            {
                Destroy(gameObject);
                return;
            }

            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            transform.rotation = Quaternion.LookRotation(transform.position - _camera.transform.position);
        }
    }
}
