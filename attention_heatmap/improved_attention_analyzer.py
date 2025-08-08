#!/usr/bin/env python3
"""
Improved Attention Analyzer
基于刘世超的实验发现改进的attention分析器

核心改进：
1. 使用更强的Qwen3-4B模型
2. 分析第一层attention（最接近原始token语义）
3. 排除首个token的权重干扰
4. 实现"每帧top3"的可读化输出
5. 支持可预期的故事接续任务
"""

import torch
import numpy as np
from transformers import AutoTokenizer, AutoModelForCausalLM
import matplotlib.pyplot as plt
from typing import Dict, List, Tuple
import json

class ImprovedAttentionAnalyzer:
    """改进的attention分析器"""
    
    def __init__(self, model_path: str = r"W:\LLM\Qwen3-4B"):
        print(f"🚀 Loading improved model: {model_path}")
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        
        self.tokenizer = AutoTokenizer.from_pretrained(model_path)
        self.model = AutoModelForCausalLM.from_pretrained(
            model_path,
            torch_dtype=torch.float16,
            device_map="auto",
            attn_implementation="eager"
        )
        
        if self.tokenizer.pad_token is None:
            self.tokenizer.pad_token = self.tokenizer.eos_token
            
        print(f"✅ Improved analyzer ready with {model_path}")
    
    def extract_clean_generation_attention(self, context: str, max_new_tokens: int = 15) -> Dict:
        """提取干净的generation attention，排除首个token干扰"""
        print(f"🔍 Extracting clean attention for: {context[:50]}...")
        
        inputs = self.tokenizer(context, return_tensors="pt", padding=False).to(self.device)
        context_tokens = self.tokenizer.convert_ids_to_tokens(inputs.input_ids[0])
        context_length = len(context_tokens)
        
        print(f"📝 Context tokens: {context_tokens}")
        print(f"📏 Context length: {context_length}")
        
        generation_data = {
            "context": context,
            "context_tokens": context_tokens,
            "context_length": context_length,
            "generated_tokens": [],
            "generation_steps": []
        }
        
        input_ids = inputs.input_ids
        
        for step in range(max_new_tokens):
            with torch.no_grad():
                outputs = self.model(input_ids, output_attentions=True, use_cache=False)
            
            # 获取下一个token
            next_token_logits = outputs.logits[0, -1, :]
            next_token_id = torch.argmax(next_token_logits, dim=-1).unsqueeze(0).unsqueeze(0)
            next_token = self.tokenizer.convert_ids_to_tokens([next_token_id.item()])[0]
            
            # 提取第一层attention（最接近原始语义）
            first_layer_att = outputs.attentions[0][0]  # (num_heads, seq_len, seq_len)
            
            # 最后一个token（当前生成的）对context的attention
            last_token_att = first_layer_att[:, -1, :context_length]  # (num_heads, context_length)
            
            # 对所有head求平均
            avg_attention = torch.mean(last_token_att, dim=0).cpu().numpy()  # (context_length,)
            
            # 排除首个token，重新归一化
            if context_length > 1:
                clean_attention = avg_attention[1:]  # 排除首个token
                clean_attention = clean_attention / np.sum(clean_attention)  # 重新归一化
                clean_context_tokens = context_tokens[1:]  # 对应的token列表
            else:
                clean_attention = avg_attention
                clean_context_tokens = context_tokens
            
            # 获取top3相关token
            top3_indices = np.argsort(clean_attention)[-3:][::-1]  # 降序
            top3_tokens = [(clean_context_tokens[i], clean_attention[i], i+1)
                          for i in top3_indices if i < len(clean_context_tokens)]

            # 解码token为可读文本
            readable_generated_token = self.tokenizer.decode([next_token_id.item()], skip_special_tokens=True)
            readable_top3 = []
            for token, weight, pos in top3_tokens:
                # 尝试解码单个token
                try:
                    readable_token = self.tokenizer.decode(self.tokenizer.convert_tokens_to_ids([token]), skip_special_tokens=True)
                    if not readable_token.strip():  # 如果解码为空，保持原token
                        readable_token = token
                except:
                    readable_token = token
                readable_top3.append((readable_token, weight, pos))

            step_data = {
                "step": step,
                "generated_token": next_token,
                "readable_generated_token": readable_generated_token,
                "raw_attention": avg_attention.tolist(),
                "clean_attention": clean_attention.tolist(),
                "top3_related_tokens": top3_tokens,
                "readable_top3_tokens": readable_top3
            }

            generation_data["generation_steps"].append(step_data)
            generation_data["generated_tokens"].append(next_token)

            # 输出当前步骤的top3 - 使用可读版本
            print(f"Step {step+1}: '{readable_generated_token}' -> Top3: {[(t[0], f'{t[1]:.3f}') for t in readable_top3]}")
            
            # 更新input_ids
            input_ids = torch.cat([input_ids, next_token_id], dim=1)
            
            if next_token_id.item() == self.tokenizer.eos_token_id:
                break
        
        return generation_data
    
    def create_story_continuation_test(self) -> str:
        """创建小红帽故事接续测试用例"""
        story_context = """我是小红帽，今天要去看望生病的奶奶。我正在森林里的小屋中准备礼物。突然，我听到了敲门声。我心想这么晚了会是谁呢？我"""
        return story_context
    
    def create_technical_context_test(self) -> str:
        """创建技术上下文测试用例"""
        tech_context = """MemoTree系统使用分层LOD节点管理认知上下文。每个节点包含Title、Brief、Detail、Full四个详细度级别。系统根据attention权重动态调整节点的展开状态。当前正在处理的任务是"""
        return tech_context
    
    def visualize_clean_attention_flow(self, generation_data: Dict):
        """可视化清理后的attention流"""
        steps = generation_data["generation_steps"]
        context_tokens = generation_data["context_tokens"][1:]  # 排除首个token
        generated_tokens = generation_data["generated_tokens"]
        
        if not steps:
            return
        
        # 构建attention矩阵
        num_steps = len(steps)
        num_context_tokens = len(context_tokens)
        
        attention_matrix = np.zeros((num_steps, num_context_tokens))
        
        for step_idx, step_data in enumerate(steps):
            clean_attention = step_data["clean_attention"]
            attention_matrix[step_idx, :len(clean_attention)] = clean_attention
        
        # 创建热力图
        plt.figure(figsize=(16, 10))
        
        # 使用对数尺度增强可视化
        log_attention = np.log1p(attention_matrix)
        
        im = plt.imshow(log_attention.T, aspect='auto', cmap='plasma', interpolation='nearest')
        
        plt.title('Clean Generation Attention Flow (First Layer, Excluding First Token)\n'
                 'Y-axis: Context Tokens, X-axis: Generated Tokens', 
                 fontsize=14, fontweight='bold')
        plt.xlabel('Generation Steps', fontsize=12)
        plt.ylabel('Context Tokens (Excluding First)', fontsize=12)
        
        # 设置x轴标签
        x_labels = [f"Step{i+1}\n{token[:6]}" for i, token in enumerate(generated_tokens)]
        plt.xticks(range(len(x_labels)), x_labels, rotation=45, ha='right')
        
        # 设置y轴标签
        y_step = max(1, num_context_tokens // 15)
        y_indices = range(0, num_context_tokens, y_step)
        y_labels = [context_tokens[i][:8] for i in y_indices if i < len(context_tokens)]
        plt.yticks(y_indices, y_labels)
        
        # 颜色条
        cbar = plt.colorbar(im)
        cbar.set_label('Log(1 + Clean Attention)', rotation=270, labelpad=20)
        
        plt.tight_layout()
        plt.savefig('clean_attention_flow.png', dpi=300, bbox_inches='tight')
        plt.show()
        
        print("📊 Clean attention flow saved as clean_attention_flow.png")
    
    def generate_readable_attention_report(self, generation_data: Dict) -> str:
        """生成可读的attention报告"""
        report_lines = []
        report_lines.append("🎯 Generation Attention Analysis Report")
        report_lines.append("=" * 50)
        report_lines.append(f"Context: {generation_data['context']}")
        report_lines.append(f"Generated: {' '.join(generation_data['generated_tokens'])}")
        report_lines.append("")
        
        report_lines.append("📊 Step-by-Step Attention Analysis:")
        report_lines.append("-" * 40)

        for step_data in generation_data["generation_steps"]:
            step = step_data["step"]
            readable_token = step_data.get("readable_generated_token", step_data["generated_token"])
            readable_top3 = step_data.get("readable_top3_tokens", step_data["top3_related_tokens"])

            report_lines.append(f"Step {step+1}: Generated '{readable_token}'")
            report_lines.append("  Most related context tokens:")
            for i, (related_token, weight, pos) in enumerate(readable_top3):
                report_lines.append(f"    {i+1}. '{related_token}' (pos {pos}): {weight:.4f}")
            report_lines.append("")
        
        report_text = "\n".join(report_lines)
        
        # 保存报告
        with open("attention_analysis_report.txt", "w", encoding="utf-8") as f:
            f.write(report_text)
        
        print("📋 Readable report saved as attention_analysis_report.txt")
        return report_text
    
    def run_comprehensive_test(self):
        """运行综合测试"""
        print("🧪 Starting Comprehensive Attention Analysis")
        print("=" * 60)
        
        # 测试1: 小红帽故事
        print("\n📚 Test 1: Little Red Riding Hood Story")
        story_context = self.create_story_continuation_test()
        story_data = self.extract_clean_generation_attention(story_context, max_new_tokens=12)
        
        print("\n📊 Visualizing story attention...")
        self.visualize_clean_attention_flow(story_data)
        
        print("\n📋 Generating story report...")
        story_report = self.generate_readable_attention_report(story_data)
        
        # 测试2: 技术上下文
        print("\n🔧 Test 2: Technical Context")
        tech_context = self.create_technical_context_test()
        tech_data = self.extract_clean_generation_attention(tech_context, max_new_tokens=10)
        
        print("\n📊 Visualizing technical attention...")
        self.visualize_clean_attention_flow(tech_data)
        
        # 保存完整结果
        results = {
            "story_test": story_data,
            "technical_test": tech_data,
            "model_info": {
                "model_path": "W:/LLM/Qwen3-4B",
                "analysis_layer": "first_layer",
                "excludes_first_token": True
            }
        }
        
        with open("comprehensive_attention_results.json", "w", encoding="utf-8") as f:
            json.dump(results, f, indent=2, ensure_ascii=False)
        
        print("💾 Comprehensive results saved!")
        return results

def main():
    analyzer = ImprovedAttentionAnalyzer()
    results = analyzer.run_comprehensive_test()
    return results

if __name__ == "__main__":
    results = main()
