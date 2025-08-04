#!/usr/bin/env python3
"""
MemoTree Attention-Driven LOD Experiment
基于LLM内在attention权重的动态热力图生成实验

目标：验证从模型attention层提取权重并映射到认知节点的可行性
"""

import torch
import numpy as np
from transformers import AutoTokenizer, AutoModelForCausalLM
from typing import Dict, List, Tuple, Optional
import json
from pathlib import Path
import matplotlib.pyplot as plt
import seaborn as sns

class AttentionHeatmapExtractor:
    """从LLM attention层提取热力图的核心类"""
    
    def __init__(self, model_path: str = r"W:\LLM\Llama-3.2-3B-Instruct"):
        print(f"🚀 Loading model from {model_path}")
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        print(f"📱 Using device: {self.device}")
        
        # 加载tokenizer和model
        self.tokenizer = AutoTokenizer.from_pretrained(model_path)
        self.model = AutoModelForCausalLM.from_pretrained(
            model_path,
            torch_dtype=torch.float16,
            device_map="auto",
            output_attentions=True  # 关键：启用attention输出
        )
        
        # 确保pad_token存在
        if self.tokenizer.pad_token is None:
            self.tokenizer.pad_token = self.tokenizer.eos_token
            
        print(f"✅ Model loaded successfully!")
        print(f"📊 Model layers: {self.model.config.num_hidden_layers}")
        print(f"🔢 Attention heads: {self.model.config.num_attention_heads}")
    
    def extract_generation_attention(self, context: str, max_new_tokens: int = 20,
                                   max_context_length: int = 512) -> Dict:
        """提取generation阶段每个输出token对context的attention分布"""
        print(f"🔍 Extracting generation attention for context: {context[:100]}...")

        # Tokenize context
        context_inputs = self.tokenizer(
            context,
            return_tensors="pt",
            max_length=max_context_length,
            truncation=True,
            padding=False
        ).to(self.device)

        context_tokens = self.tokenizer.convert_ids_to_tokens(context_inputs.input_ids[0])
        context_length = len(context_tokens)

        print(f"📏 Context length: {context_length} tokens")
        print(f"🎯 Generating {max_new_tokens} tokens...")

        # 存储每个生成token的attention
        generation_attentions = []  # List[Dict] - 每个生成token的attention数据
        generated_tokens = []

        # 初始输入
        input_ids = context_inputs.input_ids

        for step in range(max_new_tokens):
            print(f"🔄 Generation step {step + 1}/{max_new_tokens}")

            # 前向传播获取下一个token和attention
            with torch.no_grad():
                outputs = self.model(input_ids, output_attentions=True, use_cache=True)

            # 获取下一个token
            next_token_logits = outputs.logits[0, -1, :]
            next_token_id = torch.argmax(next_token_logits, dim=-1).unsqueeze(0).unsqueeze(0)
            next_token = self.tokenizer.convert_ids_to_tokens([next_token_id.item()])[0]

            # 提取当前生成token对所有之前token的attention
            # attentions: (num_layers, batch_size, num_heads, seq_len, seq_len)
            current_attentions = outputs.attentions
            current_seq_len = input_ids.shape[1]

            # 提取最后一个位置(当前生成token)对context部分的attention
            token_attention_data = {
                "step": step,
                "generated_token": next_token,
                "generated_token_id": next_token_id.item(),
                "context_attention": []  # 每层每头对context的attention
            }

            for layer_idx, layer_att in enumerate(current_attentions):
                # layer_att: (batch_size, num_heads, seq_len, seq_len)
                layer_att = layer_att[0]  # Remove batch dim: (num_heads, seq_len, seq_len)

                # 提取最后一个token(当前生成的)对context部分的attention
                last_token_att = layer_att[:, -1, :context_length]  # (num_heads, context_length)

                # 对所有head求平均得到这一层对context的attention分布
                layer_context_att = torch.mean(last_token_att, dim=0).cpu().numpy()  # (context_length,)

                token_attention_data["context_attention"].append(layer_context_att)

            generation_attentions.append(token_attention_data)
            generated_tokens.append(next_token)

            # 更新input_ids用于下一步生成
            input_ids = torch.cat([input_ids, next_token_id], dim=1)

            # 早停条件
            if next_token_id.item() == self.tokenizer.eos_token_id:
                print(f"🛑 EOS token generated at step {step + 1}")
                break

        print(f"✅ Generated {len(generated_tokens)} tokens: {generated_tokens}")

        return {
            "context": context,
            "context_tokens": context_tokens,
            "context_length": context_length,
            "generated_tokens": generated_tokens,
            "generation_attentions": generation_attentions,
            "total_steps": len(generation_attentions)
        }
    
    def aggregate_attention_to_heatmap(self, attention_data: Dict, 
                                     aggregation_method: str = "mean_last_layer") -> Dict[str, float]:
        """将attention权重聚合为token级别的热力图"""
        tokens = attention_data["tokens"]
        attentions = attention_data["attentions"]
        
        if aggregation_method == "mean_last_layer":
            # 使用最后一层的平均attention
            last_layer_att = attentions[-1]  # (num_heads, seq_len, seq_len)
            # 对所有head求平均，然后对每个token的incoming attention求和
            token_attention = np.mean(last_layer_att, axis=0).sum(axis=0)
            
        elif aggregation_method == "max_across_layers":
            # 跨层取最大attention
            all_layers_att = np.stack([np.mean(att, axis=0).sum(axis=0) for att in attentions])
            token_attention = np.max(all_layers_att, axis=0)
            
        elif aggregation_method == "weighted_layers":
            # 给后面的层更高权重
            weights = np.linspace(0.1, 1.0, len(attentions))
            weighted_att = []
            for i, att in enumerate(attentions):
                layer_att = np.mean(att, axis=0).sum(axis=0) * weights[i]
                weighted_att.append(layer_att)
            token_attention = np.sum(weighted_att, axis=0)
        
        # 归一化到0-1范围
        if token_attention.max() > 0:
            token_attention = token_attention / token_attention.max()
        
        # 构建token到attention权重的映射
        heatmap = {}
        for i, token in enumerate(tokens):
            if i < len(token_attention):
                heatmap[f"token_{i}_{token}"] = float(token_attention[i])
        
        return heatmap
    
    def simulate_concept_mapping(self, heatmap: Dict[str, float], 
                               concept_nodes: List[str]) -> Dict[str, float]:
        """模拟将token级attention映射到概念节点的过程"""
        print("🗺️ Simulating token-to-concept mapping...")
        
        # 简化的概念映射：基于关键词匹配
        concept_attention = {concept: 0.0 for concept in concept_nodes}
        
        for token_key, attention_weight in heatmap.items():
            token = token_key.split("_")[-1].lower().strip("▁")  # 处理SentencePiece token
            
            # 简单的关键词匹配策略
            for concept in concept_nodes:
                if token in concept.lower() or concept.lower() in token:
                    concept_attention[concept] += attention_weight
                    
        # 归一化
        max_attention = max(concept_attention.values()) if concept_attention.values() else 1.0
        if max_attention > 0:
            concept_attention = {k: v/max_attention for k, v in concept_attention.items()}
            
        return concept_attention
    
    def visualize_heatmap(self, heatmap: Dict[str, float], title: str = "Attention Heatmap"):
        """可视化attention热力图"""
        if not heatmap:
            print("⚠️ Empty heatmap, skipping visualization")
            return
            
        # 准备数据
        items = list(heatmap.keys())
        values = list(heatmap.values())
        
        # 创建图表
        plt.figure(figsize=(12, 8))
        colors = plt.cm.Reds(np.array(values))
        
        bars = plt.bar(range(len(items)), values, color=colors)
        plt.title(title, fontsize=16, fontweight='bold')
        plt.xlabel("Tokens/Concepts", fontsize=12)
        plt.ylabel("Attention Weight", fontsize=12)
        
        # 设置x轴标签
        plt.xticks(range(len(items)), [item.split("_")[-1][:10] for item in items], 
                  rotation=45, ha='right')
        
        # 添加数值标签
        for i, (bar, value) in enumerate(zip(bars, values)):
            if value > 0.1:  # 只显示较高的值
                plt.text(bar.get_x() + bar.get_width()/2, bar.get_height() + 0.01, 
                        f'{value:.2f}', ha='center', va='bottom', fontsize=8)
        
        plt.tight_layout()
        plt.savefig(f"attention_heatmap_{title.replace(' ', '_').lower()}.png", 
                   dpi=300, bbox_inches='tight')
        plt.show()
        print(f"📊 Heatmap saved as attention_heatmap_{title.replace(' ', '_').lower()}.png")

def run_experiment():
    """运行完整的attention热力图提取实验"""
    print("🧪 Starting MemoTree Attention-Driven LOD Experiment")
    print("=" * 60)
    
    # 初始化提取器
    extractor = AttentionHeatmapExtractor()
    
    # 测试文本：模拟复杂的认知任务
    test_text = """
    In the context of artificial intelligence and cognitive architectures, 
    the concept of attention mechanisms plays a crucial role in determining 
    which information should be prioritized during processing. Memory systems 
    in AI agents need to dynamically adjust their level of detail based on 
    the current cognitive focus, similar to how human attention works.
    """
    
    # 模拟的概念节点（在真实MemoTree中这些来自认知图谱）
    concept_nodes = [
        "Artificial Intelligence",
        "Cognitive Architecture", 
        "Attention Mechanisms",
        "Memory Systems",
        "Information Processing",
        "AI Agents",
        "Human Cognition"
    ]
    
    print(f"🎯 Test concepts: {concept_nodes}")
    print()
    
    # Step 1: 提取attention权重
    attention_data = extractor.extract_attention_weights(test_text)
    
    # Step 2: 聚合为热力图
    print("🔥 Generating attention heatmaps...")
    heatmaps = {}
    
    for method in ["mean_last_layer", "max_across_layers", "weighted_layers"]:
        print(f"📊 Using aggregation method: {method}")
        heatmap = extractor.aggregate_attention_to_heatmap(attention_data, method)
        heatmaps[method] = heatmap
        
        # 显示top-5 tokens
        top_tokens = sorted(heatmap.items(), key=lambda x: x[1], reverse=True)[:5]
        print(f"🔝 Top 5 tokens: {top_tokens}")
        print()
    
    # Step 3: 映射到概念节点
    print("🗺️ Mapping to concept nodes...")
    concept_heatmaps = {}
    
    for method, token_heatmap in heatmaps.items():
        concept_heatmap = extractor.simulate_concept_mapping(token_heatmap, concept_nodes)
        concept_heatmaps[method] = concept_heatmap
        
        print(f"📈 {method} - Concept attention:")
        for concept, attention in sorted(concept_heatmap.items(), key=lambda x: x[1], reverse=True):
            if attention > 0:
                print(f"  {concept}: {attention:.3f}")
        print()
    
    # Step 4: 可视化
    print("📊 Generating visualizations...")
    for method, concept_heatmap in concept_heatmaps.items():
        extractor.visualize_heatmap(concept_heatmap, f"Concept Attention - {method}")
    
    # Step 5: 保存结果
    results = {
        "test_text": test_text,
        "concept_nodes": concept_nodes,
        "token_heatmaps": heatmaps,
        "concept_heatmaps": concept_heatmaps,
        "model_info": {
            "model_path": extractor.model.config.name_or_path,
            "num_layers": extractor.model.config.num_hidden_layers,
            "num_heads": extractor.model.config.num_attention_heads
        }
    }
    
    with open("attention_experiment_results.json", "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)
    
    print("💾 Results saved to attention_experiment_results.json")
    print("🎉 Experiment completed successfully!")
    
    return results

if __name__ == "__main__":
    results = run_experiment()
