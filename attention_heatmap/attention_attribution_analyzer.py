#!/usr/bin/env python3
"""
Attention Attribution Analyzer
多层attention归因分析 - 类似PageRank的逐层向前归因

核心思想：
1. 每一层的attention不是独立的，而是基于前一层的表征
2. 需要通过矩阵乘法链式归因到原始token
3. 类似PageRank算法，计算token的"重要性传播"
"""

import torch
import numpy as np
from transformers import AutoTokenizer, AutoModelForCausalLM
import matplotlib.pyplot as plt
from typing import Dict, List, Tuple
import json

class AttentionAttributionAnalyzer:
    """多层attention归因分析器"""
    
    # def __init__(self, model_path: str = r"W:\LLM\Llama-3.2-3B-Instruct"):
    def __init__(self, model_path: str = r"W:\LLM\Qwen3-4B"):
        print(f"🔗 Loading model for attribution analysis: {model_path}")
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
            
        print(f"✅ Attribution analyzer ready")
    
    def extract_layered_attention_matrices(self, context: str) -> Dict:
        """提取所有层的attention矩阵用于归因分析"""
        print(f"🔍 Extracting layered attention matrices...")
        
        inputs = self.tokenizer(context, return_tensors="pt", padding=False).to(self.device)
        context_tokens = self.tokenizer.convert_ids_to_tokens(inputs.input_ids[0])
        
        with torch.no_grad():
            outputs = self.model(**inputs, output_attentions=True)
        
        # 提取所有层的attention矩阵
        attention_matrices = []
        for layer_att in outputs.attentions:
            # layer_att: (batch_size, num_heads, seq_len, seq_len)
            layer_att = layer_att[0].cpu().numpy()  # Remove batch dim
            
            # 对所有head求平均得到 (seq_len, seq_len) 矩阵
            avg_layer_att = np.mean(layer_att, axis=0)
            attention_matrices.append(avg_layer_att)
        
        return {
            "context": context,
            "context_tokens": context_tokens,
            "attention_matrices": attention_matrices,  # List of (seq_len, seq_len) matrices
            "num_layers": len(attention_matrices),
            "seq_len": len(context_tokens)
        }
    
    def compute_attribution_via_matrix_chain(self, attention_data: Dict, 
                                           target_position: int = -1) -> Dict:
        """通过矩阵链乘法计算归因 - 类似PageRank"""
        print(f"🔗 Computing attribution via matrix chain multiplication...")
        
        attention_matrices = attention_data["attention_matrices"]
        context_tokens = attention_data["context_tokens"]
        seq_len = attention_data["seq_len"]
        
        if target_position == -1:
            target_position = seq_len - 1  # 最后一个token
        
        print(f"🎯 Target position: {target_position} (token: {context_tokens[target_position]})")
        
        # 方法1: 逐层累积归因 (类似PageRank的迭代)
        # 初始化：目标位置的归因向量
        current_attribution = np.zeros(seq_len)
        current_attribution[target_position] = 1.0  # 目标token初始权重为1
        
        # 从最后一层向前传播归因
        layer_attributions = []
        
        for layer_idx in reversed(range(len(attention_matrices))):
            att_matrix = attention_matrices[layer_idx]
            
            # 归因传播：当前归因 = 前一层归因 × attention矩阵
            # 注意：这里需要转置，因为我们要计算"谁对当前token有贡献"
            new_attribution = np.dot(current_attribution, att_matrix)
            
            layer_attributions.append({
                "layer": layer_idx,
                "attribution_vector": new_attribution.copy(),
                "top_contributors": self._get_top_contributors(new_attribution, context_tokens, top_k=5)
            })
            
            current_attribution = new_attribution
            
            print(f"Layer {layer_idx}: Top contributor = {layer_attributions[-1]['top_contributors'][0]}")
        
        # 反转列表，使其从第0层到最后一层
        layer_attributions.reverse()
        
        # 方法2: 直接矩阵链乘法 (所有层的复合效应)
        print("🔗 Computing direct matrix chain multiplication...")
        
        # 计算所有attention矩阵的乘积
        composite_matrix = np.eye(seq_len)  # 单位矩阵
        
        for att_matrix in attention_matrices:
            composite_matrix = np.dot(composite_matrix, att_matrix)
        
        # 目标位置的最终归因
        final_attribution = composite_matrix[target_position, :]
        
        return {
            "target_position": target_position,
            "target_token": context_tokens[target_position],
            "layer_by_layer_attribution": layer_attributions,
            "composite_matrix": composite_matrix,
            "final_attribution": final_attribution,
            "final_top_contributors": self._get_top_contributors(final_attribution, context_tokens, top_k=10)
        }
    
    def _get_top_contributors(self, attribution_vector: np.ndarray, 
                            tokens: List[str], top_k: int = 5) -> List[Tuple[str, float, int]]:
        """获取贡献最大的tokens"""
        indexed_attributions = [(tokens[i], attribution_vector[i], i) 
                              for i in range(len(attribution_vector))]
        return sorted(indexed_attributions, key=lambda x: x[1], reverse=True)[:top_k]
    
    def visualize_attribution_evolution(self, attribution_result: Dict):
        """可视化归因在各层的演化过程"""
        layer_attributions = attribution_result["layer_by_layer_attribution"]
        context_tokens = [item[0] for item in attribution_result["final_top_contributors"]][:20]  # 只显示前20个
        
        # 构建矩阵: (layers, tokens)
        num_layers = len(layer_attributions)
        num_tokens = len(context_tokens)
        
        attribution_matrix = np.zeros((num_layers, num_tokens))
        
        for layer_idx, layer_data in enumerate(layer_attributions):
            attribution_vector = layer_data["attribution_vector"]
            
            # 只取前20个token的归因值
            for token_idx in range(min(num_tokens, len(attribution_vector))):
                attribution_matrix[layer_idx, token_idx] = attribution_vector[token_idx]
        
        # 创建热力图
        plt.figure(figsize=(16, 10))
        
        # 使用对数尺度
        log_matrix = np.log1p(np.abs(attribution_matrix))  # 取绝对值避免负数
        
        im = plt.imshow(log_matrix, aspect='auto', cmap='plasma', interpolation='nearest')
        
        plt.title(f'Attribution Evolution Across Layers (Log Scale)\n'
                 f'Target: {attribution_result["target_token"]}', 
                 fontsize=14, fontweight='bold')
        plt.xlabel('Context Tokens', fontsize=12)
        plt.ylabel('Model Layers', fontsize=12)
        
        # 设置标签
        plt.xticks(range(num_tokens), [token[:8] for token in context_tokens], 
                  rotation=45, ha='right')
        plt.yticks(range(0, num_layers, max(1, num_layers//10)), 
                  [f"Layer {i}" for i in range(0, num_layers, max(1, num_layers//10))])
        
        # 颜色条
        cbar = plt.colorbar(im)
        cbar.set_label('Log(1 + |Attribution|)', rotation=270, labelpad=20)
        
        plt.tight_layout()
        plt.savefig('attribution_evolution.png', dpi=300, bbox_inches='tight')
        plt.show()
        
        print("📊 Attribution evolution saved as attribution_evolution.png")
    
    def analyze_attribution_patterns(self, context: str, target_positions: List[int] = None) -> Dict:
        """分析多个位置的归因模式"""
        print("🔍 Analyzing attribution patterns...")
        
        # 提取attention矩阵
        attention_data = self.extract_layered_attention_matrices(context)
        
        if target_positions is None:
            # 默认分析最后几个token
            seq_len = attention_data["seq_len"]
            target_positions = list(range(max(0, seq_len-3), seq_len))
        
        results = {}
        
        for pos in target_positions:
            if pos < attention_data["seq_len"]:
                print(f"\n🎯 Analyzing position {pos}...")
                attribution_result = self.compute_attribution_via_matrix_chain(
                    attention_data, target_position=pos
                )
                results[f"position_{pos}"] = attribution_result
                
                # 可视化这个位置的归因演化
                self.visualize_attribution_evolution(attribution_result)
        
        # 保存结果
        with open("attribution_analysis_results.json", "w", encoding="utf-8") as f:
            # 转换numpy数组为列表以便JSON序列化
            serializable_results = {}
            for key, result in results.items():
                serializable_result = result.copy()
                serializable_result["final_attribution"] = result["final_attribution"].tolist()
                serializable_result["composite_matrix"] = result["composite_matrix"].tolist()
                
                # 转换layer_by_layer_attribution
                for layer_data in serializable_result["layer_by_layer_attribution"]:
                    layer_data["attribution_vector"] = layer_data["attribution_vector"].tolist()
                
                serializable_results[key] = serializable_result
            
            json.dump(serializable_results, f, indent=2, ensure_ascii=False)
        
        print("💾 Attribution analysis saved as attribution_analysis_results.json")
        
        return results

def run_attribution_analysis():
    """运行完整的归因分析"""
    print("🔗 Starting MemoTree Attribution Analysis")
    print("=" * 60)
    
    analyzer = AttentionAttributionAnalyzer()
    
    # 测试用例
    test_context = "MemoTree uses hierarchical LOD nodes for cognitive context management."
    
    # 运行归因分析
    results = analyzer.analyze_attribution_patterns(test_context)
    
    # 输出关键发现
    print("\n🔍 Attribution Analysis Summary:")
    for pos_key, result in results.items():
        print(f"\n{pos_key} (token: {result['target_token']}):")
        print("  Top 3 final contributors:")
        for i, (token, attribution, idx) in enumerate(result['final_top_contributors'][:3]):
            print(f"    {i+1}. {token}: {attribution:.6f}")
    
    return results

if __name__ == "__main__":
    results = run_attribution_analysis()
