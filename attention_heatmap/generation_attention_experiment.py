#!/usr/bin/env python3
"""
MemoTree Generation Attention Experiment
基于LLM generation阶段attention权重的动态热力图分析

核心创新：分析每个输出token对输入context的1D attention分布
构建2D频谱图：Y轴=输入tokens，X轴=输出tokens
"""

import torch
import numpy as np
from transformers import AutoTokenizer, AutoModelForCausalLM
from typing import Dict, List, Tuple, Optional
import json
from pathlib import Path
import matplotlib.pyplot as plt

class GenerationAttentionExtractor:
    """从LLM generation阶段提取动态attention的核心类"""
    
    # def __init__(self, model_path: str = r"W:\LLM\Llama-3.2-3B-Instruct"):
    def __init__(self, model_path: str = r"W:\LLM\Qwen3-4B"):
        print(f"🚀 Loading model from {model_path}")
        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        print(f"📱 Using device: {self.device}")
        
        # 加载tokenizer和model
        self.tokenizer = AutoTokenizer.from_pretrained(model_path)
        self.model = AutoModelForCausalLM.from_pretrained(
            model_path,
            torch_dtype=torch.float16,
            device_map="auto",
            attn_implementation="eager"  # 强制使用eager attention以支持output_attentions
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
        # context_tokens = self.tokenizer.convert_ids_to_tokens(context_inputs.input_ids[0][1:]) # 实验去掉首个<|begin_of_text|>。结果是新的首个token又获得了相似程度的权重。两次输出的token序列完全相同。
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
                outputs = self.model(input_ids, output_attentions=True, use_cache=False)
            
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
    
    def visualize_generation_attention_spectrogram(self, generation_data: Dict, 
                                                  layer_idx: int = -1, title_suffix: str = ""):
        """可视化generation阶段的attention频谱图 - 类似刘世超描述的2D图"""
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
            # attention_matrix[step_idx, 1:] = layer_attention[1:] # 实验排除初始权重高的离谱的<|begin_of_text|>
        
        # 创建频谱图 - 使用对数尺度避免过曝
        plt.figure(figsize=(16, 10))

        # 对attention矩阵应用对数变换，避免<|begin_of_text|>过曝
        log_attention_matrix = np.log1p(attention_matrix)  # log1p = log(1+x) 避免log(0)

        # 使用imshow创建热力图
        im = plt.imshow(log_attention_matrix.T, aspect='auto', cmap='hot', interpolation='nearest')
        
        # 设置标签
        plt.title(f'MemoTree Generation Attention Spectrogram (Log Scale){title_suffix}\n'
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
        cbar.set_label('Log(1 + Attention Weight)', rotation=270, labelpad=20)
        
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
    """运行generation阶段attention分析实验"""
    print("🧪 Starting MemoTree Generation Attention Experiment")
    print("=" * 60)
    
    # 初始化提取器
    extractor = GenerationAttentionExtractor()
    
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
        layer_idx=0,  # 使用第一层
        title_suffix=" - First Layer"
    )
    
    # Step 3: 分析动态attention模式
    print("🔍 Analyzing dynamic patterns...")
    pattern_analysis = extractor.analyze_dynamic_attention_patterns(generation_data)
    
    return generation_data, attention_matrix, pattern_analysis

if __name__ == "__main__":
    # 运行generation实验
    generation_data, attention_matrix, pattern_analysis = run_generation_experiment()
