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
    
    def extract_attention_weights(self, text: str, max_length: int = 512) -> Dict:
        """提取文本的attention权重矩阵"""
        print(f"🔍 Extracting attention for text: {text[:100]}...")
        
        # Tokenize输入
        inputs = self.tokenizer(
            text, 
            return_tensors="pt", 
            max_length=max_length,
            truncation=True,
            padding=True
        ).to(self.device)
        
        # 前向传播，获取attention权重
        with torch.no_grad():
            outputs = self.model(**inputs, output_attentions=True)
        
        # 提取attention权重 (num_layers, batch_size, num_heads, seq_len, seq_len)
        attentions = outputs.attentions
        tokens = self.tokenizer.convert_ids_to_tokens(inputs.input_ids[0])
        
        print(f"📏 Sequence length: {len(tokens)}")
        print(f"🧠 Attention layers: {len(attentions)}")
        
        return {
            "tokens": tokens,
            "input_ids": inputs.input_ids[0].cpu().numpy(),
            "attentions": [att[0].cpu().numpy() for att in attentions],  # Remove batch dim
            "text": text
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
    
    def visualize_generation_attention_spectrogram(self, generation_data: Dict,
                                                  layer_idx: int = -1, title_suffix: str = ""):
        """可视化generation阶段的attention频谱图 - 类似你描述的2D图"""
        context_tokens = generation_data["context_tokens"]
        generation_attentions = generation_data["generation_attentions"]
        generated_tokens = generation_data["generated_tokens"]

        if not generation_attentions:
            print("⚠️ No generation attention data")
            return

        # 构建2D attention矩阵: (生成步数, context长度)
        num_steps = len(generation_attentions)
        context_length = len(context_tokens)

        attention_matrix = np.zeros((num_steps, context_length))

        for step_idx, step_data in enumerate(generation_attentions):
            context_attention = step_data["context_attention"]

            if layer_idx == -1:
                # 使用最后一层
                layer_attention = context_attention[-1]
            else:
                # 使用指定层
                layer_attention = context_attention[layer_idx] if layer_idx < len(context_attention) else context_attention[-1]

            attention_matrix[step_idx, :] = layer_attention

        # 创建频谱图
        plt.figure(figsize=(16, 10))

        # 使用imshow创建热力图
        im = plt.imshow(attention_matrix.T, aspect='auto', cmap='hot', interpolation='nearest')

        # 设置标签
        plt.title(f'Generation Attention Spectrogram{title_suffix}\n'
                 f'Y-axis: Context Tokens, X-axis: Generated Tokens',
                 fontsize=14, fontweight='bold')
        plt.xlabel('Generation Steps', fontsize=12)
        plt.ylabel('Context Tokens', fontsize=12)

        # 设置x轴标签(生成的tokens)
        x_labels = [f"Step{i+1}\n{token[:8]}" for i, token in enumerate(generated_tokens)]
        plt.xticks(range(len(x_labels)), x_labels, rotation=45, ha='right')

        # 设置y轴标签(context tokens) - 只显示部分以避免过于密集
        y_step = max(1, context_length // 20)  # 最多显示20个标签
        y_indices = range(0, context_length, y_step)
        y_labels = [context_tokens[i][:10] for i in y_indices]
        plt.yticks(y_indices, y_labels)

        # 添加颜色条
        cbar = plt.colorbar(im)
        cbar.set_label('Attention Weight', rotation=270, labelpad=20)

        plt.tight_layout()

        # 保存图片
        filename = f"generation_attention_spectrogram{title_suffix.replace(' ', '_').lower()}.png"
        plt.savefig(filename, dpi=300, bbox_inches='tight')
        plt.show()
        print(f"📊 Spectrogram saved as {filename}")

        return attention_matrix

    def analyze_dynamic_attention_patterns(self, generation_data: Dict) -> Dict:
        """分析动态attention模式 - 这是MemoTree LOD调整的核心"""
        context_tokens = generation_data["context_tokens"]
        generation_attentions = generation_data["generation_attentions"]

        print("🔍 Analyzing dynamic attention patterns...")

        # 分析每个context token在整个生成过程中的attention变化
        context_attention_evolution = {}

        for token_idx, token in enumerate(context_tokens):
            token_attention_over_time = []

            for step_data in generation_attentions:
                # 使用最后一层的attention
                last_layer_attention = step_data["context_attention"][-1]
                token_attention_over_time.append(last_layer_attention[token_idx])

            context_attention_evolution[f"token_{token_idx}_{token}"] = {
                "attention_sequence": token_attention_over_time,
                "max_attention": max(token_attention_over_time),
                "min_attention": min(token_attention_over_time),
                "attention_variance": np.var(token_attention_over_time),
                "final_attention": token_attention_over_time[-1] if token_attention_over_time else 0
            }

        # 识别高动态性的tokens(attention变化大的)
        high_dynamic_tokens = sorted(
            context_attention_evolution.items(),
            key=lambda x: x[1]["attention_variance"],
            reverse=True
        )[:5]

        # 识别持续高attention的tokens
        high_attention_tokens = sorted(
            context_attention_evolution.items(),
            key=lambda x: x[1]["max_attention"],
            reverse=True
        )[:5]

        print("🎯 Top 5 High Dynamic Tokens (变化最大):")
        for token_key, stats in high_dynamic_tokens:
            token_name = token_key.split("_")[-1]
            print(f"  {token_name}: variance={stats['attention_variance']:.4f}, "
                  f"max={stats['max_attention']:.4f}")

        print("🔥 Top 5 High Attention Tokens (注意力最高):")
        for token_key, stats in high_attention_tokens:
            token_name = token_key.split("_")[-1]
            print(f"  {token_name}: max={stats['max_attention']:.4f}, "
                  f"final={stats['final_attention']:.4f}")

        return {
            "context_attention_evolution": context_attention_evolution,
            "high_dynamic_tokens": high_dynamic_tokens,
            "high_attention_tokens": high_attention_tokens
        }

def run_generation_experiment():
    """运行新的generation阶段attention分析实验"""
    print("🧪 Starting MemoTree Generation Attention Experiment")
    print("=" * 60)

    # 初始化提取器
    extractor = AttentionHeatmapExtractor()

    # 测试context：模拟MemoTree的认知上下文
    context = """MemoTree is a cognitive context management system for LLM agents. It uses hierarchical LOD (Level of Detail) nodes that can be dynamically expanded or collapsed based on attention patterns. The system integrates with Git for version control and supports semantic relationships between cognitive nodes."""

    print(f"📝 Context: {context}")
    print()

    # Step 1: 提取generation阶段的attention
    print("🚀 Extracting generation attention...")
    generation_data = extractor.extract_generation_attention(
        context=context,
        max_new_tokens=15,
        max_context_length=256
    )

    print(f"✅ Generated tokens: {generation_data['generated_tokens']}")
    print()

    # Step 2: 可视化attention频谱图
    print("📊 Creating attention spectrogram...")
    attention_matrix = extractor.visualize_generation_attention_spectrogram(
        generation_data,
        layer_idx=-1,  # 使用最后一层
        title_suffix=" - Last Layer"
    )

    # Step 3: 分析动态attention模式
    print("🔍 Analyzing dynamic patterns...")
    pattern_analysis = extractor.analyze_dynamic_attention_patterns(generation_data)

    # Step 4: 模拟LOD调整决策
    print("🎯 Simulating LOD adjustment decisions...")

    # 基于attention分析结果模拟LOD调整
    lod_decisions = []

    for token_key, stats in pattern_analysis["high_attention_tokens"]:
        token_name = token_key.split("_")[-1]
        max_attention = stats["max_attention"]
        final_attention = stats["final_attention"]

        # 简单的LOD调整逻辑
        if max_attention > 0.1 and final_attention > 0.05:
            decision = "EXPAND"  # 高attention且持续 -> 展开
        elif max_attention < 0.02:
            decision = "COLLAPSE"  # 低attention -> 折叠
        else:
            decision = "MAINTAIN"  # 中等attention -> 保持

        lod_decisions.append({
            "token": token_name,
            "max_attention": max_attention,
            "final_attention": final_attention,
            "lod_decision": decision
        })

    print("📋 LOD Adjustment Decisions:")
    for decision in lod_decisions:
        print(f"  {decision['token']}: {decision['lod_decision']} "
              f"(max={decision['max_attention']:.4f}, final={decision['final_attention']:.4f})")

    # Step 5: 保存完整结果
    results = {
        "context": context,
        "generation_data": {
            "context_tokens": generation_data["context_tokens"],
            "generated_tokens": generation_data["generated_tokens"],
            "total_steps": generation_data["total_steps"]
        },
        "attention_matrix": attention_matrix.tolist(),
        "pattern_analysis": {
            "high_dynamic_tokens": [(k, v) for k, v in pattern_analysis["high_dynamic_tokens"]],
            "high_attention_tokens": [(k, v) for k, v in pattern_analysis["high_attention_tokens"]]
        },
        "lod_decisions": lod_decisions,
        "model_info": {
            "model_path": extractor.model.config.name_or_path,
            "num_layers": extractor.model.config.num_hidden_layers,
            "num_heads": extractor.model.config.num_attention_heads
        }
    }

    with open("generation_attention_results.json", "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)

    print("💾 Results saved to generation_attention_results.json")
    print("🎉 Generation experiment completed successfully!")

    return results

if __name__ == "__main__":
    # 运行新的generation实验
    results = run_generation_experiment()
