#!/usr/bin/env python3
"""
Attention Anomaly Analyzer
深度分析attention权重的异常模式，特别是<|begin_of_text|>的高权重问题

核心问题：
1. <|begin_of_text|>为什么有如此高的attention权重？
2. 这是模型的设计特性还是计算错误？
3. 如何正确解释和处理这种现象？
"""

import torch
import numpy as np
from transformers import AutoTokenizer, AutoModelForCausalLM
import matplotlib.pyplot as plt
import seaborn as sns
from typing import Dict, List, Tuple
import json

class AttentionAnomalyAnalyzer:
    """专门分析attention权重异常的工具"""
    
    def __init__(self, model_path: str = r"W:\LLM\Llama-3.2-3B-Instruct"):
        print(f"🔍 Loading model for anomaly analysis: {model_path}")
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
            
        print(f"✅ Model loaded for anomaly analysis")
    
    def analyze_bos_token_attention(self, context: str, max_new_tokens: int = 10) -> Dict:
        """专门分析<|begin_of_text|>token的attention模式"""
        print(f"🔍 Analyzing BOS token attention for: {context[:50]}...")
        
        # Tokenize
        inputs = self.tokenizer(context, return_tensors="pt", padding=False).to(self.device)
        context_tokens = self.tokenizer.convert_ids_to_tokens(inputs.input_ids[0])
        
        print(f"📝 Context tokens: {context_tokens[:10]}...")  # 显示前10个
        
        bos_analysis = {
            "context": context,
            "context_tokens": context_tokens,
            "bos_token_index": 0 if context_tokens[0].startswith('<|begin_of_text|>') else -1,
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
            
            # 分析每一层对BOS token的attention
            step_analysis = {
                "step": step,
                "generated_token": next_token,
                "bos_attention_by_layer": [],
                "total_attention_by_layer": [],
                "bos_attention_ratio_by_layer": []
            }
            
            current_seq_len = input_ids.shape[1]
            
            for layer_idx, layer_att in enumerate(outputs.attentions):
                # layer_att: (batch_size, num_heads, seq_len, seq_len)
                layer_att = layer_att[0]  # Remove batch dim
                
                # 最后一个token(当前生成的)的attention分布
                last_token_att = layer_att[:, -1, :]  # (num_heads, seq_len)
                
                # 对所有head求平均
                avg_att = torch.mean(last_token_att, dim=0).cpu().numpy()  # (seq_len,)
                
                # BOS token的attention (通常是index 0)
                bos_attention = avg_att[0] if len(avg_att) > 0 else 0.0
                total_attention = np.sum(avg_att)
                bos_ratio = bos_attention / total_attention if total_attention > 0 else 0.0
                
                step_analysis["bos_attention_by_layer"].append(float(bos_attention))
                step_analysis["total_attention_by_layer"].append(float(total_attention))
                step_analysis["bos_attention_ratio_by_layer"].append(float(bos_ratio))
            
            bos_analysis["generation_steps"].append(step_analysis)
            
            # 更新input_ids
            input_ids = torch.cat([input_ids, next_token_id], dim=1)
            
            print(f"Step {step+1}: {next_token}, BOS ratio in last layer: {step_analysis['bos_attention_ratio_by_layer'][-1]:.4f}")
        
        return bos_analysis
    
    def analyze_attention_distribution_patterns(self, context: str) -> Dict:
        """分析attention分布的统计特性"""
        print(f"📊 Analyzing attention distribution patterns...")
        
        inputs = self.tokenizer(context, return_tensors="pt", padding=False).to(self.device)
        
        with torch.no_grad():
            outputs = self.model(**inputs, output_attentions=True)
        
        context_tokens = self.tokenizer.convert_ids_to_tokens(inputs.input_ids[0])
        attentions = outputs.attentions
        
        analysis = {
            "context": context,
            "context_tokens": context_tokens,
            "num_layers": len(attentions),
            "layer_analysis": []
        }
        
        for layer_idx, layer_att in enumerate(attentions):
            # layer_att: (batch_size, num_heads, seq_len, seq_len)
            layer_att = layer_att[0].cpu().numpy()  # Remove batch dim
            
            # 分析这一层的attention模式
            layer_stats = {
                "layer": layer_idx,
                "attention_entropy": [],  # 每个位置的attention熵
                "bos_attention_strength": [],  # 每个位置对BOS的attention
                "attention_concentration": []  # attention的集中度
            }
            
            seq_len = layer_att.shape[-1]
            
            for pos in range(seq_len):
                # 这个位置对所有之前位置的attention (causal mask)
                pos_attention = np.mean(layer_att[:, pos, :pos+1], axis=0)  # 平均所有head
                
                if len(pos_attention) > 0 and np.sum(pos_attention) > 0:
                    # 计算熵 (attention分布的均匀程度)
                    normalized_att = pos_attention / np.sum(pos_attention)
                    entropy = -np.sum(normalized_att * np.log(normalized_att + 1e-10))
                    
                    # BOS attention强度
                    bos_strength = pos_attention[0] if len(pos_attention) > 0 else 0.0
                    
                    # 注意力集中度 (最大attention值)
                    concentration = np.max(pos_attention)
                    
                    layer_stats["attention_entropy"].append(float(entropy))
                    layer_stats["bos_attention_strength"].append(float(bos_strength))
                    layer_stats["attention_concentration"].append(float(concentration))
            
            analysis["layer_analysis"].append(layer_stats)
        
        return analysis
    
    def visualize_bos_attention_evolution(self, bos_analysis: Dict):
        """可视化BOS token attention在生成过程中的演化"""
        steps = len(bos_analysis["generation_steps"])
        layers = len(bos_analysis["generation_steps"][0]["bos_attention_ratio_by_layer"])
        
        # 构建矩阵: (steps, layers)
        bos_ratio_matrix = np.zeros((steps, layers))
        
        for step_idx, step_data in enumerate(bos_analysis["generation_steps"]):
            bos_ratio_matrix[step_idx, :] = step_data["bos_attention_ratio_by_layer"]
        
        # 创建热力图
        plt.figure(figsize=(14, 8))
        
        # 使用对数尺度
        log_matrix = np.log1p(bos_ratio_matrix)
        
        im = plt.imshow(log_matrix.T, aspect='auto', cmap='viridis', interpolation='nearest')
        
        plt.title('BOS Token Attention Ratio Evolution (Log Scale)\nY-axis: Model Layers, X-axis: Generation Steps', 
                 fontsize=14, fontweight='bold')
        plt.xlabel('Generation Steps', fontsize=12)
        plt.ylabel('Model Layers', fontsize=12)
        
        # 设置x轴标签
        generated_tokens = [step["generated_token"] for step in bos_analysis["generation_steps"]]
        x_labels = [f"Step{i+1}\n{token[:6]}" for i, token in enumerate(generated_tokens)]
        plt.xticks(range(len(x_labels)), x_labels, rotation=45, ha='right')
        
        # 设置y轴标签
        plt.yticks(range(0, layers, max(1, layers//10)), 
                  [f"Layer {i}" for i in range(0, layers, max(1, layers//10))])
        
        # 颜色条
        cbar = plt.colorbar(im)
        cbar.set_label('Log(1 + BOS Attention Ratio)', rotation=270, labelpad=20)
        
        plt.tight_layout()
        plt.savefig('bos_attention_evolution.png', dpi=300, bbox_inches='tight')
        plt.show()
        
        print("📊 BOS attention evolution saved as bos_attention_evolution.png")
    
    def investigate_bos_anomaly(self, context: str) -> Dict:
        """综合调查BOS token attention异常的完整分析"""
        print("🕵️ Starting comprehensive BOS anomaly investigation...")
        
        # 1. BOS attention分析
        bos_analysis = self.analyze_bos_token_attention(context, max_new_tokens=8)
        
        # 2. 整体attention分布分析
        distribution_analysis = self.analyze_attention_distribution_patterns(context)
        
        # 3. 可视化
        self.visualize_bos_attention_evolution(bos_analysis)
        
        # 4. 生成报告
        report = {
            "investigation_summary": {
                "context": context,
                "bos_token_detected": bos_analysis["bos_token_index"] >= 0,
                "avg_bos_ratio_last_layer": np.mean([
                    step["bos_attention_ratio_by_layer"][-1] 
                    for step in bos_analysis["generation_steps"]
                ]),
                "max_bos_ratio_last_layer": np.max([
                    step["bos_attention_ratio_by_layer"][-1] 
                    for step in bos_analysis["generation_steps"]
                ])
            },
            "bos_analysis": bos_analysis,
            "distribution_analysis": distribution_analysis
        }
        
        # 保存详细报告
        with open("bos_anomaly_investigation.json", "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, ensure_ascii=False)
        
        print("📋 Investigation complete! Report saved as bos_anomaly_investigation.json")
        
        return report

def run_anomaly_investigation():
    """运行完整的attention异常调查"""
    print("🕵️ Starting MemoTree Attention Anomaly Investigation")
    print("=" * 60)
    
    analyzer = AttentionAnomalyAnalyzer()
    
    # 测试用例
    test_context = "MemoTree is a cognitive context management system for LLM agents."
    
    # 运行调查
    report = analyzer.investigate_bos_anomaly(test_context)
    
    # 输出关键发现
    summary = report["investigation_summary"]
    print("\n🔍 Key Findings:")
    print(f"  BOS token detected: {summary['bos_token_detected']}")
    print(f"  Average BOS ratio (last layer): {summary['avg_bos_ratio_last_layer']:.4f}")
    print(f"  Maximum BOS ratio (last layer): {summary['max_bos_ratio_last_layer']:.4f}")
    
    return report

if __name__ == "__main__":
    report = run_anomaly_investigation()
